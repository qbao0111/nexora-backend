using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

using static Nexora.Data.Practice.InterviewErrors;
using static Nexora.Data.Practice.InterviewPersistence;

namespace Nexora.Data.Practice;

public sealed partial class InterviewReportJobHandler(
    NexoraDbContext dbContext,
    InterviewPersistence persistence,
    IResumeContextBuilder resumeContextBuilder,
    IStructuredAiExecutor structuredAiExecutor,
    IAiProvider aiProvider,
    TimeProvider timeProvider,
    ILogger<PracticeService> logger)
{
    internal Task ProcessAsync(OutboxEvent job, CancellationToken cancellationToken) => BuildReportAsync(job, cancellationToken);
    private void MarkProcessed(OutboxEvent job) { job.Status = BillingValues.Processed; job.ProcessedAt = timeProvider.GetUtcNow(); }

    private async Task BuildReportAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.AsNoTracking().Include(item => item.Questions).ThenInclude(question => question.Answer)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (await dbContext.InterviewReports.AsNoTracking().AnyAsync(item => item.InterviewSessionId == snapshot.Id, cancellationToken))
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }
        if (snapshot.Status == PracticeValues.Completed)
            throw InvalidState();

        var answeredQuestions = snapshot.Questions
            .Where(item => item.Answer is not null && !string.IsNullOrWhiteSpace(item.Answer.Content))
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (answeredQuestions.Length < MinimumReportAnswers)
            throw InvalidState();
        var transcript = string.Join("\n", answeredQuestions
            .Select(item => $"Q: {item.Content}\nA: {item.Answer!.Content}"));
        // Reports must be grounded in the observed interview answers. Resume
        // profile claims are not interview evidence and are intentionally not
        // included in the synthesis input.
        var reportContext = resumeContextBuilder.BuildReportContext(transcript, profile: null);
        var groundingTranscript = string.Join("\n", answeredQuestions.Select(item => item.Answer!.Content));
        var reportOperationContext = new AiOperationContext(
            snapshot.Id.ToString("N"),
            snapshot.UserId,
            GroundingTranscript: groundingTranscript);
        AiExecutionResult<InterviewReportOutput> execResult;
        var fallbackUsed = false;
        try
        {
            execResult = await structuredAiExecutor.ExecuteAsync(
                AiOperations.InterviewReport,
                reportContext,
                reportOperationContext,
                cancellationToken);
        }
        catch (BusinessException ex) when (string.Equals(ex.Code, "AI_OUTPUT_INVALID", StringComparison.Ordinal))
        {
            var fallback = BuildDeterministicReportFallback(answeredQuestions, reportOperationContext);
            if (fallback is null)
                throw;
            execResult = fallback;
            fallbackUsed = true;
        }
        var output = execResult.Value;
        ValidateScores(output.Scores);
        if (output.Gaps.Count == 0 || output.ActionPlan.Count == 0) throw InvalidAiOutput();
        var overall = WeightedScore(output.Scores);

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var session = await persistence.FindInterviewForUpdateAsync(snapshot.UserId, job.AggregateId, cancellationToken) ?? throw NotFound();
        var existing = await dbContext.InterviewReports.SingleOrDefaultAsync(item => item.InterviewSessionId == session.Id, cancellationToken);
        if (existing is not null)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return;
        }
        if (session.Status != PracticeValues.Completing) throw InvalidState();
        var now = timeProvider.GetUtcNow();
        dbContext.InterviewReports.Add(new InterviewReport
        {
            Id = Guid.NewGuid(),
            UserId = session.UserId,
            InterviewSessionId = session.Id,
            OverallScore = overall,
            Rubric = JsonSerializer.Serialize(output.Scores, JsonOptions),
            Strengths = JsonSerializer.Serialize(output.Strengths, JsonOptions),
            Gaps = JsonSerializer.Serialize(output.Gaps, JsonOptions),
            ActionPlan = JsonSerializer.Serialize(output.ActionPlan, JsonOptions),
            Disclaimer = BuildReportDisclaimer(answeredQuestions.Length, snapshot.Questions.Count),
            ModelVersion = execResult.ModelVersion,
            PromptVersion = execResult.PromptVersion,
            RubricVersion = execResult.RubricVersion,
            SchemaVersion = execResult.SchemaVersion,
            CreatedAt = now
        });
        session.Status = PracticeValues.Completed;
        persistence.EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
        session.Version++;
        session.UpdatedAt = now;
        session.CompletedAt = now;
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        if (fallbackUsed)
            ReportFallbackPersisted(logger, AiOperations.InterviewReport.Purpose, aiProvider.ModelVersion, session.Id, reportOperationContext.CorrelationId);
    }

    private static void ValidateScores(IReadOnlyCollection<RubricScore> scores)
    {
        var required = new[] { "correctness", "structure", "completeness", "clarity" };
        if (scores.Count != required.Length || required.Any(name => scores.Count(item => item.Criterion == name) != 1) ||
            scores.Any(item => item.Score is < 0 or > 100 || string.IsNullOrWhiteSpace(item.Evidence))) throw InvalidAiOutput();
    }

    private static AiExecutionResult<InterviewReportOutput>? BuildDeterministicReportFallback(
        InterviewQuestion[] answeredQuestions,
        AiOperationContext context)
    {
        if (answeredQuestions.Any(question => question.Answer?.EvaluationStatus != InterviewAnswerEvaluationStates.Ready))
            return null;

        var evaluations = answeredQuestions
            .Select(question => new
            {
                Question = question,
                Answer = question.Answer!,
                Evaluation = TryDeserializeAnswerEvaluation(question.Answer!.Evaluation)
            })
            .Where(item => item.Evaluation is not null)
            .ToArray();
        if (evaluations.Length != answeredQuestions.Length)
            return null;

        var scores = new List<RubricScore>(CanonicalRubricValidator.RequiredCriteria.Length);
        foreach (var criterion in CanonicalRubricValidator.RequiredCriteria)
        {
            var candidates = evaluations
                .Select(item => new
                {
                    item.Question.Sequence,
                    item.Answer.Id,
                    Score = item.Evaluation!.Scores.SingleOrDefault(score =>
                        string.Equals(score.Criterion, criterion, StringComparison.Ordinal))
                })
                .ToArray();
            if (candidates.Any(item => item.Score is null))
                return null;

            var aggregate = (int)Math.Round(
                candidates.Average(item => item.Score!.Score),
                MidpointRounding.AwayFromZero);
            var evidence = candidates
                .OrderBy(item => Math.Abs(item.Score!.Score - aggregate))
                .ThenBy(item => item.Sequence)
                .ThenBy(item => item.Id)
                .Select(item => item.Score!.Evidence.Trim())
                .FirstOrDefault(item => AnswerCoachingValidator.IsGroundedReportEvidence(item, context.GroundingTranscript!));
            evidence ??= evaluations
                .OrderBy(item => item.Question.Sequence)
                .ThenBy(item => item.Answer.Id)
                .Select(item => item.Answer.Content.Trim()[..Math.Min(item.Answer.Content.Trim().Length, 500)])
                .FirstOrDefault(item => AnswerCoachingValidator.IsGroundedReportEvidence(item, context.GroundingTranscript!));
            if (string.IsNullOrWhiteSpace(evidence))
                return null;
            scores.Add(new RubricScore(criterion, aggregate, evidence));
        }

        var strengths = evaluations
            .OrderBy(item => item.Question.Sequence)
            .ThenBy(item => item.Answer.Id)
            .SelectMany(item => item.Evaluation!.Strengths ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => item.Length is > 0 and <= 500 &&
                AnswerCoachingValidator.IsGroundedReportStrength(item, context.GroundingTranscript!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        var improvements = evaluations
            .OrderBy(item => item.Question.Sequence)
            .ThenBy(item => item.Answer.Id)
            .SelectMany(item => item.Evaluation!.Improvements ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => item.Length is > 0 and <= 500)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        var weakestCriterion = scores.OrderBy(item => item.Score).First().Criterion;
        var gap = weakestCriterion switch
        {
            "structure" => "Cần trình bày câu trả lời theo cấu trúc rõ ràng hơn.",
            "completeness" => "Cần đề cập đầy đủ các ý của câu hỏi.",
            "clarity" => "Cần diễn đạt lập luận rõ và ít mơ hồ hơn.",
            _ => "Cần kiểm tra giả định và giải thích cơ sở của câu trả lời."
        };
        var action = weakestCriterion switch
        {
            "structure" => "Hãy sắp xếp câu trả lời theo mở đầu, lập luận và kết luận.",
            "completeness" => "Hãy đối chiếu từng phần của câu hỏi và bổ sung phần còn thiếu.",
            "clarity" => "Hãy nêu trực tiếp ý chính và giải thích từng bước lập luận.",
            _ => "Hãy nêu rõ giả định và kiểm tra cơ sở kỹ thuật hoặc nghiệp vụ."
        };

        var fallback = new InterviewReportOutput(
            scores,
            strengths,
            improvements.Length > 0 ? improvements : [gap],
            improvements.Length > 0 ? improvements : [action],
            AiOperations.ScoreScale);
        var validation = AiOperations.InterviewReport.NormalizeAndValidate(fallback, context);
        if (!validation.IsValid)
            return null;

        return new AiExecutionResult<InterviewReportOutput>(
            validation.NormalizedValue!,
            "deterministic:validated-answer-aggregate-v2",
            "interview-report-fallback-v2",
            AiOperations.InterviewReport.SchemaVersion,
            AiOperations.InterviewReport.RubricVersion,
            true,
            AiOperations.InterviewReport.MaxAttempts,
            0);
    }

    private static int WeightedScore(IReadOnlyCollection<RubricScore> scores)
    {
        var values = scores.ToDictionary(item => item.Criterion, item => item.Score, StringComparer.Ordinal);
        return (int)Math.Round(values["correctness"] * .40 + values["structure"] * .25 + values["completeness"] * .20 + values["clarity"] * .15);
    }

    private static string BuildReportDisclaimer(int answeredQuestions, int issuedQuestions) =>
        InterviewReportService.IsPartialReport(answeredQuestions, issuedQuestions)
            ? $"{Disclaimer} This is a partial sample based on {answeredQuestions} of {issuedQuestions} answered questions; it is not a full competency assessment."
            : $"{Disclaimer} This coaching report is based on {answeredQuestions} answered questions.";

    internal async Task FailAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var current = await dbContext.OutboxEvents.SingleAsync(item => item.Id == job.Id, cancellationToken);
        current.Status = PracticeValues.Failed;
        current.ProcessedAt = timeProvider.GetUtcNow();
        {
            var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
            var sourceId = session.Id.ToString("N");
            var credited = await dbContext.UsageEvents.AsNoTracking().AnyAsync(item => item.EntitlementId == reservation.EntitlementId &&
                item.Action == BillingValues.Adjustment && item.SourceId == sourceId, cancellationToken);
            if (!credited)
            {
                var entitlement = await persistence.FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
                entitlement.Adjustment++;
                entitlement.UpdatedAt = current.ProcessedAt.Value;
                entitlement.ConcurrencyToken = Guid.NewGuid();
                dbContext.UsageEvents.Add(new UsageEvent
                {
                    Id = Guid.NewGuid(),
                    UserId = session.UserId,
                    EntitlementId = entitlement.Id,
                    Action = BillingValues.Adjustment,
                    Quantity = 1,
                    SourceType = "report_failure",
                    SourceId = sourceId,
                    IdempotencyKey = $"report-failure:{sourceId}",
                    Reason = "Terminal report generation failure credit",
                    CreatedAt = current.ProcessedAt.Value
                });
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    [LoggerMessage(LogLevel.Warning,
        "Interview report recovered with deterministic fallback. purpose={Purpose} model={ModelVersion} interviewId={InterviewId} outcome=persisted_fallback correlationId={CorrelationId}")]
    private static partial void ReportFallbackPersisted(
        ILogger logger, string purpose, string modelVersion, Guid interviewId, string correlationId);
}
