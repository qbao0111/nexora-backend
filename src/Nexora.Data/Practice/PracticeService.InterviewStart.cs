using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class PracticeService
{
    public async Task<InterviewView> StartInterviewAsync(
        Guid userId, StartInterviewCommand command, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(command);
        var prior = await FindIdempotentAsync(userId, "interview.start", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        var context = await ResolveInterviewStartContextAsync(userId, command, cancellationToken);
        return await CreateInterviewAsync(userId, context, key, fingerprint, "interview.start", cancellationToken);
    }

    private async Task<InterviewView> CreateInterviewAsync(
        Guid userId,
        InterviewStartContext context,
        string key,
        string fingerprint,
        string operation,
        CancellationToken cancellationToken)
    {
        ValidateInterview(context.Role, context.Seniority, context.InterviewType, context.Difficulty);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        var prior = await FindIdempotentAsync(userId, operation, key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        await ValidateOwnedContextAsync(userId, context.ResumeId, context.JobDescriptionId, cancellationToken);
        var entitlement = await FindActiveEntitlementForUpdateAsync(userId, cancellationToken)
            ?? throw new BusinessException("QUOTA_EXCEEDED", "Bạn đã dùng hết lượt phỏng vấn của gói hiện tại.", BusinessErrorKind.Forbidden);
        if (Available(entitlement) < 1)
            throw new BusinessException("QUOTA_EXCEEDED", "Bạn đã dùng hết lượt phỏng vấn của gói hiện tại.", BusinessErrorKind.Forbidden);

        var now = timeProvider.GetUtcNow();
        var sessionId = Guid.NewGuid();
        var reservation = new UsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementId = entitlement.Id,
            Action = BillingValues.Reserve,
            Quantity = 1,
            SourceType = "interview",
            SourceId = sessionId.ToString("N"),
            // Keep reservation keys distinct from legacy interview starts even
            // when a client reuses an idempotency key across operations.
            IdempotencyKey = operation == "interview.start"
                ? $"interview:{key}"
                : $"interview-practice:{key}",
            CreatedAt = now
        };
        var session = new InterviewSession
        {
            Id = sessionId,
            UserId = userId,
            ResumeId = context.ResumeId,
            JobDescriptionId = context.JobDescriptionId,
            CareerGoalId = context.CareerGoalId,
            SourceInterviewId = context.SourceInterviewId,
            SourceQuestionId = context.SourceQuestionId,
            PracticeReason = context.PracticeReason,
            FocusTopic = context.FocusTopic,
            ReservationEventId = reservation.Id,
            ReservationEvent = reservation,
            Role = context.Role,
            Seniority = context.Seniority,
            InterviewType = context.InterviewType,
            Difficulty = context.Difficulty,
            Status = PracticeValues.Starting,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        entitlement.Reserved++;
        entitlement.UpdatedAt = now;
        entitlement.ConcurrencyToken = Guid.NewGuid();
        dbContext.AddRange(reservation, session, Idempotency(userId, operation, key, fingerprint, session.Id, now),
            Outbox("InterviewStartRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return MapInterview(session, [], []);
    }

    private async Task<InterviewStartContext> ResolveInterviewStartContextAsync(
        Guid userId,
        StartInterviewCommand command,
        CancellationToken cancellationToken)
    {
        var role = TrimToNull(command.Role);
        var seniority = TrimToNull(command.Seniority);
        var interviewType = TrimToNull(command.InterviewType);
        var difficulty = TrimToNull(command.Difficulty);
        var resumeId = command.ResumeId;
        var jobDescriptionId = command.JobDescriptionId;

        if (command.CareerGoalId is { } careerGoalId)
        {
            var goal = await dbContext.CareerGoals.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == careerGoalId && item.UserId == userId && item.DeletedAt == null, cancellationToken)
                ?? throw new BusinessException("CAREER_GOAL_NOT_FOUND", "Không tìm thấy career goal.", BusinessErrorKind.NotFound);

            role ??= TrimToNull(goal.TargetRole);
            seniority ??= TrimToNull(goal.Seniority);
            jobDescriptionId ??= goal.TargetJobDescriptionId;
            var primaryResumeId = await dbContext.UserProfiles.AsNoTracking()
                .Where(item => item.UserId == userId)
                .Select(item => item.PrimaryResumeId)
                .SingleOrDefaultAsync(cancellationToken);
            if (resumeId is null && primaryResumeId is { } selectedResumeId &&
                await dbContext.Resumes.AsNoTracking().AnyAsync(item =>
                    item.Id == selectedResumeId && item.UserId == userId &&
                    item.DeletedAt == null && item.Status == PracticeValues.Ready, cancellationToken))
                resumeId = selectedResumeId;
        }

        ValidateInterview(role, seniority, interviewType, difficulty);
        return new InterviewStartContext(
            role!,
            seniority!,
            interviewType!,
            difficulty!,
            resumeId,
            jobDescriptionId,
            command.CareerGoalId,
            null,
            null,
            null,
            null);
    }

    public async Task<InterviewHistoryPage> GetInterviewHistoryAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var paging = NormalizePaging(page, pageSize);
        var query = dbContext.InterviewSessions.AsNoTracking().Where(item => item.UserId == userId);
        var totalCount = await query.CountAsync(cancellationToken);
        var skip = PagingSkip(paging.Page, paging.PageSize);
        var projectedQuery = query.Select(item => new
        {
            item.Id,
            item.Status,
            item.Role,
            item.Seniority,
            item.InterviewType,
            item.Difficulty,
            item.CreatedAt,
            item.UpdatedAt,
            item.CompletedAt,
            item.CareerGoalId,
            item.SourceInterviewId,
            item.SourceQuestionId,
            item.PracticeReason,
            item.FocusTopic
        });
        var rows = dbContext.Database.IsNpgsql()
            ? await projectedQuery
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Skip((int)Math.Min(skip, int.MaxValue))
                .Take(paging.PageSize)
                .ToArrayAsync(cancellationToken)
            : (await projectedQuery.ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Skip((int)Math.Min(skip, int.MaxValue))
                .Take(paging.PageSize)
                .ToArray();

        var ids = rows.Select(item => item.Id).ToArray();
        var answeredCounts = new Dictionary<Guid, int>();
        var issuedCounts = new Dictionary<Guid, int>();
        var reportIds = new HashSet<Guid>();
        if (ids.Length > 0)
        {
            answeredCounts = await dbContext.InterviewAnswers.AsNoTracking()
                .Where(item => item.UserId == userId && ids.Contains(item.InterviewSessionId))
                .GroupBy(item => item.InterviewSessionId)
                .Select(group => new { Id = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
            issuedCounts = await dbContext.InterviewQuestions.AsNoTracking()
                .Where(item => ids.Contains(item.InterviewSessionId) && item.ReleasedAt != null)
                .GroupBy(item => item.InterviewSessionId)
                .Select(group => new { Id = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
            reportIds = (await dbContext.InterviewReports.AsNoTracking()
                .Where(item => item.UserId == userId && ids.Contains(item.InterviewSessionId))
                .Select(item => item.InterviewSessionId)
                .ToArrayAsync(cancellationToken)).ToHashSet();
        }

        var items = rows.Select(item => new InterviewHistoryItem(
            item.Id,
            item.Status,
            item.Role,
            item.Seniority,
            item.InterviewType,
            item.Difficulty,
            item.CreatedAt,
            item.UpdatedAt,
            item.CompletedAt,
            answeredCounts.GetValueOrDefault(item.Id),
            issuedCounts.GetValueOrDefault(item.Id),
            reportIds.Contains(item.Id),
            item.CareerGoalId,
            item.SourceInterviewId,
            item.SourceQuestionId,
            item.PracticeReason,
            item.FocusTopic)).ToArray();
        return new(items, paging.Page, paging.PageSize, totalCount, skip + items.Length < totalCount);
    }

    public async Task<InterviewView> PracticeAgainAsync(
        Guid userId,
        Guid interviewId,
        PracticeAgainCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = RequireKey(idempotencyKey);
        var normalizedFocus = NormalizePracticeFocus(command.Focus);
        var normalizedReason = NormalizePracticeReason(command.Reason);
        var fingerprint = Fingerprint(interviewId, command.QuestionId, normalizedFocus, normalizedReason);
        var prior = await FindIdempotentAsync(userId, "interview.practice-again", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        var source = await dbContext.InterviewSessions.AsNoTracking()
            .Include(item => item.Questions)
            .Include(item => item.Answers)
            .Include(item => item.Report)
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        if (source.Status != PracticeValues.Completed || source.Report is null)
            throw new BusinessException(
                "PRACTICE_SOURCE_NOT_COMPLETED",
                "Chỉ có thể luyện lại từ interview đã hoàn thành và có báo cáo.",
                BusinessErrorKind.Conflict);

        Guid? sourceQuestionId = null;
        string focusTopic;
        string reason;
        if (command.QuestionId is { } questionId)
        {
            if (normalizedReason is not null && normalizedReason != InterviewPracticeValues.RepeatQuestion)
                throw Validation("Reason không khớp với yêu cầu luyện lại câu hỏi.", "PRACTICE_REASON_INVALID");
            var sourceQuestion = source.Questions.SingleOrDefault(item => item.Id == questionId)
                ?? throw NotFound();
            if (!source.Answers.Any(item => item.QuestionId == questionId && !string.IsNullOrWhiteSpace(item.Content)))
                throw new BusinessException(
                    "PRACTICE_SOURCE_QUESTION_UNANSWERED",
                    "Chỉ có thể luyện lại câu hỏi đã có câu trả lời.",
                    BusinessErrorKind.Conflict);
            focusTopic = sourceQuestion.Topic;
            if (normalizedFocus is not null && !string.Equals(normalizedFocus, focusTopic, StringComparison.OrdinalIgnoreCase))
                throw Validation("Focus không khớp với chủ đề câu hỏi nguồn.", "PRACTICE_FOCUS_INVALID");
            sourceQuestionId = sourceQuestion.Id;
            reason = InterviewPracticeValues.RepeatQuestion;
        }
        else if (normalizedFocus is not null && InterviewPracticeValues.IsSupportedRubricFocus(normalizedFocus))
        {
            if (normalizedReason == InterviewPracticeValues.RepeatQuestion)
                throw Validation("Reason không khớp với yêu cầu luyện tập.", "PRACTICE_REASON_INVALID");
            focusTopic = ResolveWeakestTopic(source, normalizedFocus);
            reason = normalizedReason ?? InterviewPracticeValues.RubricWeakness;
        }
        else if (normalizedFocus is not null)
        {
            if (normalizedReason == InterviewPracticeValues.RepeatQuestion)
                throw Validation("Reason không khớp với yêu cầu luyện tập.", "PRACTICE_REASON_INVALID");
            focusTopic = normalizedFocus;
            reason = normalizedReason ?? InterviewPracticeValues.Manual;
        }
        else
        {
            if (normalizedReason == InterviewPracticeValues.RepeatQuestion)
                throw Validation("Reason không khớp với yêu cầu luyện tập.", "PRACTICE_REASON_INVALID");
            focusTopic = ResolveWeakestTopic(source, criterion: null);
            reason = normalizedReason ?? InterviewPracticeValues.RubricWeakness;
        }

        var context = new InterviewStartContext(
            source.Role,
            source.Seniority,
            source.InterviewType,
            source.Difficulty,
            source.ResumeId,
            source.JobDescriptionId,
            source.CareerGoalId,
            source.Id,
            sourceQuestionId,
            reason,
            focusTopic);
        return await CreateInterviewAsync(
            userId,
            context,
            key,
            fingerprint,
            "interview.practice-again",
            cancellationToken);
    }

    public async Task<InterviewView> GetInterviewAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken)
    {
        var session = await dbContext.InterviewSessions.AsNoTracking()
            .Include(item => item.Questions)
            .Include(item => item.Answers)
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        ValidateQuestionContracts(session.Questions);
        var continuation = await BuildContinuationAsync(userId, session, cancellationToken);
        var reportState = await GetReportStateAsync(session.Id, session.Status, cancellationToken);
        var progress = BuildEvaluationProgress(session.Answers);
        var questionPreparationState = await GetQuestionPreparationStateAsync(userId, session, cancellationToken);
        return MapInterview(session, session.Questions, session.Answers, continuation, reportState,
            GetResultState(session.Status, reportState, progress), progress, questionPreparationState);
    }

    private static void ValidateInterview(string? role, string? seniority, string? interviewType, string? difficulty)
    {
        if (new[] { role, seniority, interviewType, difficulty }
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160))
            throw Validation("Thông tin interview không hợp lệ.");
        if (!InterviewQuestionValues.IsSupportedInterviewType(interviewType))
            throw Validation("Loại interview không hợp lệ.", "INTERVIEW_TYPE_INVALID");
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizePracticeFocus(string? focus)
    {
        var normalized = TrimToNull(focus)?.ToLowerInvariant();
        if (normalized is null) return null;
        if (normalized.StartsWith("interview.", StringComparison.Ordinal))
            normalized = normalized["interview.".Length..];

        var isTopic = normalized is
            InterviewQuestionValues.SelfIntroduction or
            InterviewQuestionValues.BehavioralStar or
            InterviewQuestionValues.MotivationRoleFit or
            InterviewQuestionValues.Technical or
            InterviewQuestionValues.Behavioral or
            InterviewQuestionValues.CvTargeted or
            InterviewQuestionValues.JdTargeted or
            InterviewQuestionValues.Scenario;
        if (!isTopic && !InterviewPracticeValues.IsSupportedRubricFocus(normalized))
            throw Validation("Focus luyện tập không hợp lệ.", "PRACTICE_FOCUS_INVALID");
        return normalized;
    }

    private static string? NormalizePracticeReason(string? reason)
    {
        var normalized = TrimToNull(reason)?.ToLowerInvariant();
        if (normalized is not null && !InterviewPracticeValues.IsSupportedReason(normalized))
            throw Validation("Nguồn luyện tập không hợp lệ.", "PRACTICE_REASON_INVALID");
        return normalized;
    }

    private static string ResolveWeakestTopic(InterviewSession source, string? criterion)
    {
        var rubric = Array.Empty<RubricScore>();
        try
        {
            rubric = JsonSerializer.Deserialize<RubricScore[]>(source.Report?.Rubric ?? string.Empty, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // Persisted reports are validated before completion. Keep a safe
            // deterministic fallback for legacy rows with malformed rubric JSON.
        }

        var rubricOrder = new[] { "correctness", "structure", "completeness", "clarity" };
        var selectedCriterion = criterion is not null && rubric.Any(item =>
                string.Equals(item.Criterion, criterion, StringComparison.OrdinalIgnoreCase))
            ? criterion
            : rubric
                .Where(item => rubricOrder.Contains(item.Criterion, StringComparer.OrdinalIgnoreCase))
                .OrderBy(item => item.Score)
                .ThenBy(item => Array.IndexOf(rubricOrder, item.Criterion.ToLowerInvariant()))
                .Select(item => item.Criterion.ToLowerInvariant())
                .FirstOrDefault();

        if (selectedCriterion is not null)
        {
            var matchingQuestion = source.Questions
                .Select(question => new
                {
                    Question = question,
                    Score = source.Answers
                        .Where(answer => answer.QuestionId == question.Id)
                        .Select(answer => TryDeserializeAnswerEvaluation(answer.Evaluation))
                        .Where(evaluation => evaluation is not null)
                        .SelectMany(evaluation => evaluation!.Scores)
                        .Where(score => string.Equals(score.Criterion, selectedCriterion, StringComparison.OrdinalIgnoreCase))
                        .Select(score => (int?)score.Score)
                        .FirstOrDefault()
                })
                .Where(item => item.Score.HasValue)
                .OrderBy(item => item.Score)
                .ThenBy(item => item.Question.Sequence)
                .ThenBy(item => item.Question.Id)
                .Select(item => item.Question.Topic)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(matchingQuestion)) return matchingQuestion;
        }

        return InterviewQuestionValues.TopicForInterviewType(source.InterviewType);
    }

    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > MaximumHistoryPageSize)
            throw Validation("Tham số phân trang không hợp lệ.", "INVALID_PAGINATION");
        return (page, pageSize);
    }

    private static long PagingSkip(int page, int pageSize) => (long)(page - 1) * pageSize;

}
