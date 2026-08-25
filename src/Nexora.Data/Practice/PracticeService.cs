using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class PracticeService(
    NexoraDbContext dbContext,
    IUploadProvider uploadProvider,
    IStorageProvider storageProvider,
    IDocumentExtractor documentExtractor,
    IAiProvider aiProvider,
    IBillingService billingService,
    TimeProvider timeProvider,
    ILogger<PracticeService> logger) : IPracticeService, IPracticeJobProcessor
{
    private const string ModelVersion = "fake-ai-v1";
    private const string PromptVersion = "phase3-v1";
    private const string SchemaVersion = "phase3-v1";
    private const string RubricVersion = "interview-rubric-v1";
    private const string Disclaimer = "Điểm số chỉ là ước lượng phục vụ coaching, không phải đánh giá tuyển dụng.";
    private static readonly JsonDocument EmptySchema = JsonDocument.Parse("{}");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonDocument QuestionSchema = JsonDocument.Parse("""{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""");
    private static readonly JsonDocument AnalysisSchema = JsonDocument.Parse("""{"type":"object","properties":{"strengths":{"type":"array","items":{"type":"string"}},"gaps":{"type":"array","items":{"type":"string"}},"recommendations":{"type":"array","items":{"type":"string"}}},"required":["strengths","gaps","recommendations"]}""");
    private static readonly JsonDocument EvaluationSchema = JsonDocument.Parse("""{"type":"object","properties":{"scores":{"type":"array","items":{"type":"object","properties":{"criterion":{"type":"string"},"score":{"type":"integer"},"evidence":{"type":"string"}},"required":["criterion","score","evidence"]}},"feedback":{"type":"string"}},"required":["scores","feedback"]}""");
    private static readonly JsonDocument ReportSchema = JsonDocument.Parse("""{"type":"object","properties":{"scores":{"type":"array","items":{"type":"object","properties":{"criterion":{"type":"string"},"score":{"type":"integer"},"evidence":{"type":"string"}},"required":["criterion","score","evidence"]}},"strengths":{"type":"array","items":{"type":"string"}},"gaps":{"type":"array","items":{"type":"string"}},"actionPlan":{"type":"array","items":{"type":"string"}}},"required":["scores","strengths","gaps","actionPlan"]}""");

    public async Task<ResumeView> CreateResumeAsync(Guid userId, string uploadToken, CancellationToken cancellationToken)
    {
        var upload = await uploadProvider.GetCompletedAsync(userId, uploadToken, cancellationToken);
        var duplicate = await dbContext.StoredFiles.AsNoTracking().SingleOrDefaultAsync(file => file.StorageKey == upload.StorageKey, cancellationToken);
        if (duplicate is not null)
        {
            var existing = await dbContext.Resumes.AsNoTracking().Include(resume => resume.StoredFile)
                .SingleAsync(resume => resume.StoredFileId == duplicate.Id && resume.UserId == userId, cancellationToken);
            return MapResume(existing);
        }

        var now = timeProvider.GetUtcNow();
        var storedFile = new StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StorageKey = upload.StorageKey,
            FileName = upload.FileName,
            ContentType = upload.ContentType,
            Size = upload.Size,
            Checksum = upload.Checksum,
            CreatedAt = now
        };
        var resume = new ResumeRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StoredFileId = storedFile.Id,
            StoredFile = storedFile,
            Status = PracticeValues.Uploaded,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.AddRange(storedFile, resume, Outbox("ResumeExtractionRequested", "resume", resume.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapResume(resume);
    }

    public async Task<JobDescriptionView> CreateJobDescriptionAsync(Guid userId, string title, string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 160 || string.IsNullOrWhiteSpace(content) || content.Trim().Length > 30_000)
            throw Validation("Job description không hợp lệ.");
        var now = timeProvider.GetUtcNow();
        var jobDescription = new JobDescription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = title.Trim(),
            Content = content.Trim(),
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.JobDescriptions.Add(jobDescription);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapJobDescription(jobDescription);
    }

    public async Task<ResumeAnalysisView> StartResumeAnalysisAsync(
        Guid userId, Guid resumeId, Guid jobDescriptionId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(resumeId, jobDescriptionId);
        var prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);
        var resume = await dbContext.Resumes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == resumeId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        if (resume.Status != PracticeValues.Ready) throw Conflict("RESUME_NOT_READY", "CV chưa sẵn sàng để phân tích.");
        var jd = await dbContext.JobDescriptions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == jobDescriptionId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        var now = timeProvider.GetUtcNow();
        var analysis = new ResumeAnalysis
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ResumeId = resume.Id,
            JobDescriptionId = jd.Id,
            ResumeVersion = resume.Version,
            JobDescriptionVersion = jd.Version,
            Status = PracticeValues.Queued,
            ModelVersion = ModelVersion,
            PromptVersion = PromptVersion,
            SchemaVersion = SchemaVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.AddRange(analysis, Idempotency(userId, "resume-analysis.create", key, fingerprint, analysis.Id, now),
            Outbox("ResumeAnalysisRequested", "resume_analysis", analysis.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapAnalysis(analysis);
    }

    public async Task<ResumeAnalysisView> GetResumeAnalysisAsync(Guid userId, Guid analysisId, CancellationToken cancellationToken)
    {
        var analysis = await dbContext.ResumeAnalyses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == analysisId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        return MapAnalysis(analysis);
    }

    public async Task<InterviewView> StartInterviewAsync(
        Guid userId, StartInterviewCommand command, string idempotencyKey, CancellationToken cancellationToken)
    {
        ValidateInterview(command);
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(command);
        var prior = await FindIdempotentAsync(userId, "interview.start", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        await ValidateOwnedContextAsync(userId, command.ResumeId, command.JobDescriptionId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindIdempotentAsync(userId, "interview.start", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
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
            IdempotencyKey = $"interview:{key}",
            CreatedAt = now
        };
        var session = new InterviewSession
        {
            Id = sessionId,
            UserId = userId,
            ResumeId = command.ResumeId,
            JobDescriptionId = command.JobDescriptionId,
            ReservationEventId = reservation.Id,
            ReservationEvent = reservation,
            Role = command.Role.Trim(),
            Seniority = command.Seniority.Trim(),
            InterviewType = command.InterviewType.Trim(),
            Difficulty = command.Difficulty.Trim(),
            Status = PracticeValues.Starting,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        entitlement.Reserved++;
        entitlement.UpdatedAt = now;
        entitlement.ConcurrencyToken = Guid.NewGuid();
        dbContext.AddRange(reservation, session, Idempotency(userId, "interview.start", key, fingerprint, session.Id, now),
            Outbox("InterviewStartRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapInterview(session, [], []);
    }

    public async Task<InterviewView> GetInterviewAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken)
    {
        var session = await dbContext.InterviewSessions.AsNoTracking()
            .Include(item => item.Questions)
            .Include(item => item.Answers)
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        return MapInterview(session, session.Questions, session.Answers);
    }

    public async Task<AnswerResult> SubmitAnswerAsync(
        Guid userId, Guid interviewId, Guid questionId, string content, int? durationSeconds, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Trim().Length > 12_000 || durationSeconds is < 0 or > 7200)
            throw Validation("Câu trả lời không hợp lệ.");
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId, questionId, content.Trim(), durationSeconds);
        var prior = await FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
        if (prior is not null) return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);

        var snapshot = await dbContext.InterviewSessions.AsNoTracking().Include(item => item.Questions).Include(item => item.Answers)
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken) ?? throw NotFound();
        if (snapshot.Status != PracticeValues.Active) throw InvalidState();
        var question = snapshot.Questions.SingleOrDefault(item => item.Id == questionId) ?? throw NotFound();
        if (snapshot.Answers.Any(item => item.QuestionId == questionId)) throw Conflict("ANSWER_ALREADY_EXISTS", "Câu hỏi đã có câu trả lời chính thức.");

        AnswerEvaluation evaluation;
        GeneratedQuestion? generated = null;
        try
        {
            evaluation = await aiProvider.GenerateStructuredAsync<AnswerEvaluation>(Request("interview.evaluate", content.Trim(), interviewId), cancellationToken);
            if (snapshot.Questions.Count < 2)
                generated = await aiProvider.GenerateStructuredAsync<GeneratedQuestion>(Request("interview.followup", content.Trim(), interviewId), cancellationToken);
        }
        catch (AiProviderException exception)
        {
            throw AiUnavailable(exception);
        }
        ValidateScores(evaluation.Scores);
        if (generated is not null && string.IsNullOrWhiteSpace(generated.Content)) throw InvalidAiOutput();

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
        if (prior is not null) return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);
        var session = await dbContext.InterviewSessions.Include(item => item.Questions).Include(item => item.Answers)
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken) ?? throw NotFound();
        if (session.Status != PracticeValues.Active || session.Answers.Any(item => item.QuestionId == questionId)) throw InvalidState();
        var now = timeProvider.GetUtcNow();
        var answer = new InterviewAnswer
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InterviewSessionId = session.Id,
            QuestionId = question.Id,
            Content = content.Trim(),
            DurationSeconds = durationSeconds,
            Evaluation = JsonSerializer.Serialize(evaluation, JsonOptions),
            CreatedAt = now
        };
        InterviewQuestion? nextQuestion = null;
        if (generated is not null)
        {
            nextQuestion = new InterviewQuestion
            {
                Id = Guid.NewGuid(),
                InterviewSessionId = session.Id,
                Sequence = session.Questions.Count + 1,
                Content = generated.Content.Trim(),
                PromptVersion = PromptVersion,
                ModelVersion = ModelVersion,
                CreatedAt = now
            };
            dbContext.InterviewQuestions.Add(nextQuestion);
        }
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(answer, Idempotency(userId, "interview.answer", key, fingerprint, answer.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AnswerResult(MapAnswer(answer), nextQuestion is null ? null : MapQuestion(nextQuestion), nextQuestion is null);
    }

    public async Task<InterviewView> CompleteInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.complete", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await dbContext.InterviewSessions.Include(item => item.Questions).Include(item => item.Answers)
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken) ?? throw NotFound();
        if (session.Status == PracticeValues.Completing)
        {
            var retryAt = timeProvider.GetUtcNow();
            dbContext.AddRange(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, retryAt),
                Outbox("InterviewReportRequested", "interview", session.Id, retryAt));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MapInterview(session, session.Questions, session.Answers);
        }
        if (session.Status != PracticeValues.Active || session.Questions.Count == 0 || session.Answers.Count != session.Questions.Count)
            throw InvalidState();
        var now = timeProvider.GetUtcNow();
        session.Status = PracticeValues.Completing;
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, now),
            Outbox("InterviewReportRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapInterview(session, session.Questions, session.Answers);
    }

    public async Task<ReportView> GetReportAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken)
    {
        var report = await dbContext.InterviewReports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.InterviewSessionId == interviewId && item.UserId == userId, cancellationToken) ?? throw NotFound();
        return MapReport(report);
    }

    public async Task<DashboardView> GetDashboardAsync(Guid userId, CancellationToken cancellationToken)
    {
        var billing = await billingService.GetSummaryAsync(userId, cancellationToken);
        var interviewRows = await dbContext.InterviewSessions.AsNoTracking().Where(item => item.UserId == userId)
            .Select(item => new InterviewSummary(item.Id, item.Role, item.Status, item.UpdatedAt)).ToArrayAsync(cancellationToken);
        var reportRows = await dbContext.InterviewReports.AsNoTracking().Where(item => item.UserId == userId)
            .Select(item => new ReportSummary(item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt)).ToArrayAsync(cancellationToken);
        var interviews = interviewRows.OrderByDescending(item => item.UpdatedAt).Take(20).ToArray();
        var reports = reportRows.OrderByDescending(item => item.CreatedAt).Take(20).ToArray();
        return new DashboardView(billing, interviews, reports);
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now.AddMinutes(-10);
        var relevant = dbContext.OutboxEvents.AsNoTracking().Where(item =>
            item.Type == "ResumeExtractionRequested" || item.Type == "ResumeAnalysisRequested" ||
            item.Type == "InterviewStartRequested" || item.Type == "InterviewReportRequested");
        var candidates = !dbContext.Database.IsNpgsql()
            ? (await relevant.ToArrayAsync(cancellationToken))
                .Where(item => item.Status == BillingValues.Pending ||
                    (item.Status == BillingValues.Processing && item.ProcessedAt <= staleBefore))
                .OrderBy(item => item.CreatedAt).Take(20).ToArray()
            : await relevant.Where(item => item.Status == BillingValues.Pending ||
                    (item.Status == BillingValues.Processing && item.ProcessedAt <= staleBefore))
                .OrderBy(item => item.CreatedAt).Take(20).ToArrayAsync(cancellationToken);
        var claimedCount = 0;
        foreach (var candidate in candidates)
        {
            var claimQuery = dbContext.OutboxEvents.Where(item => item.Id == candidate.Id && item.Status == candidate.Status);
            claimQuery = candidate.Status == BillingValues.Pending
                ? claimQuery.Where(item => item.ProcessedAt == null)
                : claimQuery.Where(item => item.ProcessedAt == candidate.ProcessedAt);
            var claimed = await claimQuery.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, BillingValues.Processing)
                .SetProperty(item => item.ProcessedAt, now), cancellationToken);
            if (claimed == 0) continue;

            claimedCount++;
            var job = await dbContext.OutboxEvents.SingleAsync(item => item.Id == candidate.Id, cancellationToken);
            var started = Stopwatch.GetTimestamp();
            var queueLagSeconds = Math.Max(0, (timeProvider.GetUtcNow() - job.CreatedAt).TotalSeconds);
            try
            {
                switch (job.Type)
                {
                    case "ResumeExtractionRequested": await ExtractResumeAsync(job, cancellationToken); break;
                    case "ResumeAnalysisRequested": await AnalyzeResumeAsync(job, cancellationToken); break;
                    case "InterviewStartRequested": await ActivateInterviewAsync(job, cancellationToken); break;
                    case "InterviewReportRequested": await BuildReportAsync(job, cancellationToken); break;
                }
                JobCompleted(logger, job.Id, job.Type, job.AggregateId, queueLagSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                await FailJobAsync(job, cancellationToken);
                JobFailed(logger, job.Id, job.Type, job.AggregateId, exception.GetType().Name,
                    queueLagSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        return claimedCount;
    }

    private async Task ExtractResumeAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var resume = await dbContext.Resumes.Include(item => item.StoredFile).SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        await using var stream = await storageProvider.OpenReadAsync(resume.StoredFile.StorageKey, cancellationToken);
        var text = await documentExtractor.ExtractAsync(stream, resume.StoredFile.ContentType, cancellationToken);
        if (string.IsNullOrWhiteSpace(text)) throw InvalidAiOutput();
        resume.ExtractedText = text;
        resume.Status = PracticeValues.Ready;
        resume.UpdatedAt = timeProvider.GetUtcNow();
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task AnalyzeResumeAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var analysis = await dbContext.ResumeAnalyses.Include(item => item.Resume).Include(item => item.JobDescription)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        analysis.Status = PracticeValues.Processing;
        analysis.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        var input = $"<resume>{Bound(analysis.Resume.ExtractedText)}</resume><job-description>{Bound(analysis.JobDescription.Content)}</job-description>";
        var result = await aiProvider.GenerateStructuredAsync<ResumeAnalysisOutput>(Request("resume.analysis", input, analysis.Id), cancellationToken);
        if (result.Strengths.Count == 0 || result.Gaps.Count == 0 || result.Recommendations.Count == 0) throw InvalidAiOutput();
        analysis.Result = JsonSerializer.Serialize(result, JsonOptions);
        analysis.Status = PracticeValues.Completed;
        analysis.CompletedAt = analysis.UpdatedAt = timeProvider.GetUtcNow();
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ActivateInterviewAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.AsNoTracking().SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (snapshot.Status != PracticeValues.Starting) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); return; }
        var generated = await aiProvider.GenerateStructuredAsync<GeneratedQuestion>(Request("interview.first-question", snapshot.Role, snapshot.Id), cancellationToken);
        if (string.IsNullOrWhiteSpace(generated.Content) || generated.Content.Length > 2_000) throw InvalidAiOutput();

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (session.Status != PracticeValues.Starting) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return; }
        var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
        var entitlement = await FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
        var now = timeProvider.GetUtcNow();
        dbContext.InterviewQuestions.Add(new InterviewQuestion
        {
            Id = Guid.NewGuid(),
            InterviewSessionId = session.Id,
            Sequence = 1,
            Content = generated.Content.Trim(),
            PromptVersion = PromptVersion,
            ModelVersion = ModelVersion,
            CreatedAt = now
        });
        FinalizeReservation(entitlement, reservation, BillingValues.Consume, now);
        session.Status = PracticeValues.Active;
        session.Version++;
        session.UpdatedAt = now;
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task BuildReportAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.AsNoTracking().Include(item => item.Questions).ThenInclude(question => question.Answer)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (snapshot.Status == PracticeValues.Completed) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); return; }
        var transcript = string.Join("\n", snapshot.Questions.OrderBy(item => item.Sequence)
            .Select(item => $"Q: {item.Content}\nA: {item.Answer?.Content}"));
        var output = await aiProvider.GenerateStructuredAsync<InterviewReportOutput>(Request("interview.report", Bound(transcript), snapshot.Id), cancellationToken);
        ValidateScores(output.Scores);
        if (output.Strengths.Count == 0 || output.Gaps.Count == 0 || output.ActionPlan.Count == 0) throw InvalidAiOutput();
        var overall = WeightedScore(output.Scores);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (session.Status == PracticeValues.Completed) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return; }
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
            Disclaimer = Disclaimer,
            ModelVersion = ModelVersion,
            PromptVersion = PromptVersion,
            RubricVersion = RubricVersion,
            SchemaVersion = SchemaVersion,
            CreatedAt = now
        });
        session.Status = PracticeValues.Completed;
        session.Version++;
        session.UpdatedAt = now;
        session.CompletedAt = now;
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task FailJobAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var current = await dbContext.OutboxEvents.SingleAsync(item => item.Id == job.Id, cancellationToken);
        current.Status = PracticeValues.Failed;
        current.ProcessedAt = timeProvider.GetUtcNow();
        if (current.Type == "InterviewStartRequested")
        {
            var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            if (session.Status == PracticeValues.Starting)
            {
                var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
                var entitlement = await FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
                FinalizeReservation(entitlement, reservation, BillingValues.Void, current.ProcessedAt.Value);
                session.Status = PracticeValues.Failed;
                session.Version++;
                session.UpdatedAt = current.ProcessedAt.Value;
            }
        }
        else if (current.Type == "ResumeExtractionRequested")
        {
            var resume = await dbContext.Resumes.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            resume.Status = PracticeValues.Failed;
            resume.UpdatedAt = current.ProcessedAt.Value;
        }
        else if (current.Type == "ResumeAnalysisRequested")
        {
            var analysis = await dbContext.ResumeAnalyses.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            analysis.Status = PracticeValues.Failed;
            analysis.ErrorCode = "AI_PROCESSING_FAILED";
            analysis.UpdatedAt = current.ProcessedAt.Value;
        }
        else if (current.Type == "InterviewReportRequested")
        {
            var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
            var sourceId = session.Id.ToString("N");
            var credited = await dbContext.UsageEvents.AsNoTracking().AnyAsync(item => item.EntitlementId == reservation.EntitlementId &&
                item.Action == BillingValues.Adjustment && item.SourceId == sourceId, cancellationToken);
            if (!credited)
            {
                var entitlement = await FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
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
        await transaction.CommitAsync(cancellationToken);
    }

    private void FinalizeReservation(Entitlement entitlement, UsageEvent reservation, string action, DateTimeOffset now)
    {
        if (entitlement.Reserved < reservation.Quantity) throw InvalidState();
        entitlement.Reserved -= reservation.Quantity;
        if (action == BillingValues.Consume) entitlement.Consumed += reservation.Quantity;
        entitlement.UpdatedAt = now;
        entitlement.ConcurrencyToken = Guid.NewGuid();
        dbContext.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = reservation.UserId,
            EntitlementId = reservation.EntitlementId,
            Action = action,
            Quantity = reservation.Quantity,
            SourceType = "reservation",
            SourceId = reservation.Id.ToString("N"),
            IdempotencyKey = $"{action}:{reservation.Id:N}",
            CreatedAt = now
        });
    }

    private async Task ValidateOwnedContextAsync(Guid userId, Guid? resumeId, Guid? jobDescriptionId, CancellationToken cancellationToken)
    {
        if (resumeId is not null && !await dbContext.Resumes.AnyAsync(item => item.Id == resumeId && item.UserId == userId && item.Status == PracticeValues.Ready, cancellationToken))
            throw NotFound();
        if (jobDescriptionId is not null && !await dbContext.JobDescriptions.AnyAsync(item => item.Id == jobDescriptionId && item.UserId == userId, cancellationToken))
            throw NotFound();
    }

    private async Task<AnswerResult> MapExistingAnswerAsync(Guid userId, Guid interviewId, Guid answerId, CancellationToken cancellationToken)
    {
        var session = await dbContext.InterviewSessions.AsNoTracking().Include(item => item.Questions).Include(item => item.Answers)
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken) ?? throw NotFound();
        var answer = session.Answers.Single(item => item.Id == answerId);
        var next = session.Questions.OrderBy(item => item.Sequence).FirstOrDefault(item => item.Sequence > session.Questions.Single(q => q.Id == answer.QuestionId).Sequence);
        return new AnswerResult(MapAnswer(answer), next is null ? null : MapQuestion(next), next is null);
    }

    private async Task<IdempotencyRecord?> FindIdempotentAsync(Guid userId, string operation, string key, string fingerprint, CancellationToken cancellationToken)
    {
        var record = await dbContext.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ActorId == userId && item.Operation == operation && item.Key == key, cancellationToken);
        if (record is not null && record.RequestFingerprint != fingerprint)
            throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
        return record;
    }

    private async Task<Entitlement?> FindActiveEntitlementForUpdateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (dbContext.Database.IsNpgsql())
            return await dbContext.Entitlements.FromSqlInterpolated(
                $"SELECT * FROM entitlements WHERE \"UserId\" = {userId} AND \"Status\" = {BillingValues.Active} AND \"StartsAt\" <= {now} AND \"EndsAt\" > {now} ORDER BY \"EndsAt\" LIMIT 1 FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        var candidates = await dbContext.Entitlements.Where(item => item.UserId == userId && item.Status == BillingValues.Active).ToArrayAsync(cancellationToken);
        return candidates.Where(item => item.StartsAt <= now && item.EndsAt > now).OrderBy(item => item.EndsAt).FirstOrDefault();
    }

    private async Task<Entitlement?> FindEntitlementForUpdateAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
            return await dbContext.Entitlements.FromSqlInterpolated($"SELECT * FROM entitlements WHERE \"Id\" = {entitlementId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        return await dbContext.Entitlements.SingleOrDefaultAsync(item => item.Id == entitlementId, cancellationToken);
    }

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);

    private static void ValidateInterview(StartInterviewCommand command)
    {
        if (new[] { command.Role, command.Seniority, command.InterviewType, command.Difficulty }
            .Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160)) throw Validation("Thông tin interview không hợp lệ.");
    }

    private static void ValidateScores(IReadOnlyCollection<RubricScore> scores)
    {
        var required = new[] { "correctness", "structure", "completeness", "clarity" };
        if (scores.Count != required.Length || required.Any(name => scores.Count(item => item.Criterion == name) != 1) ||
            scores.Any(item => item.Score is < 0 or > 100 || string.IsNullOrWhiteSpace(item.Evidence))) throw InvalidAiOutput();
    }

    private static int WeightedScore(IReadOnlyCollection<RubricScore> scores)
    {
        var values = scores.ToDictionary(item => item.Criterion, item => item.Score, StringComparer.Ordinal);
        return (int)Math.Round(values["correctness"] * .40 + values["structure"] * .25 + values["completeness"] * .20 + values["clarity"] * .15);
    }

    private static AiRequest Request(string purpose, string input, Guid correlationId)
    {
        var schema = purpose switch
        {
            "resume.analysis" => AnalysisSchema,
            "interview.evaluate" => EvaluationSchema,
            "interview.report" => ReportSchema,
            "interview.first-question" or "interview.followup" => QuestionSchema,
            _ => EmptySchema
        };
        var maxOutputTokens = purpose == "interview.report" ? 4_000 : 2_000;
        return new(purpose, PromptVersion, ModelVersion, RubricVersion, SchemaVersion, Bound(input), schema, maxOutputTokens, correlationId.ToString("N"));
    }

    private static string Bound(string? value) => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, 20_000)];
    private static int? Available(Entitlement entitlement) => entitlement.InterviewLimit is null ? null : entitlement.InterviewLimit + entitlement.Adjustment - entitlement.Reserved - entitlement.Consumed;
    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128 ? throw Validation("Idempotency-Key hợp lệ là bắt buộc.", "IDEMPOTENCY_KEY_REQUIRED") : value.Trim();
    private static string Fingerprint(params object?[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();
    private static IdempotencyRecord Idempotency(Guid userId, string operation, string key, string fingerprint, Guid resourceId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), ActorId = userId, Operation = operation, Key = key, RequestFingerprint = fingerprint, ResourceId = resourceId, CreatedAt = now };
    private static OutboxEvent Outbox(string type, string aggregateType, Guid aggregateId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), Type = type, AggregateType = aggregateType, AggregateId = aggregateId, Payload = JsonSerializer.Serialize(new { aggregateId }), Status = BillingValues.Pending, CreatedAt = now };
    private void MarkProcessed(OutboxEvent job) { job.Status = BillingValues.Processed; job.ProcessedAt = timeProvider.GetUtcNow(); }

    [LoggerMessage(LogLevel.Information,
        "Job {JobId} ({JobType}/{AggregateId}) completed after {QueueLagSeconds} queue seconds in {DurationMs} ms")]
    private static partial void JobCompleted(
        ILogger logger, Guid jobId, string jobType, Guid aggregateId, double queueLagSeconds, double durationMs);

    [LoggerMessage(LogLevel.Error,
        "Job {JobId} ({JobType}/{AggregateId}) failed with {ExceptionType} after {QueueLagSeconds} queue seconds in {DurationMs} ms")]
    private static partial void JobFailed(
        ILogger logger, Guid jobId, string jobType, Guid aggregateId, string exceptionType, double queueLagSeconds, double durationMs);

    private static ResumeView MapResume(ResumeRecord resume) => new(resume.Id, resume.StoredFile.FileName, resume.StoredFile.ContentType, resume.StoredFile.Size, resume.Status, resume.CreatedAt);
    private static JobDescriptionView MapJobDescription(JobDescription jd) => new(jd.Id, jd.Title, jd.Content, jd.CreatedAt);
    private static ResumeAnalysisView MapAnalysis(ResumeAnalysis analysis) => new(analysis.Id, analysis.Status, ParseJson(analysis.Result), analysis.CreatedAt, analysis.CompletedAt);
    private static InterviewView MapInterview(InterviewSession session, IEnumerable<InterviewQuestion> questions, IEnumerable<InterviewAnswer> answers) =>
        new(session.Id, session.Status, session.Role, session.Seniority, session.InterviewType, session.Difficulty, session.Version,
            questions.OrderBy(item => item.Sequence).Select(MapQuestion).ToArray(), answers.OrderBy(item => item.CreatedAt).Select(MapAnswer).ToArray(), session.CreatedAt, session.UpdatedAt);
    private static QuestionView MapQuestion(InterviewQuestion question) => new(question.Id, question.Sequence, question.Content, question.CreatedAt);
    private static AnswerView MapAnswer(InterviewAnswer answer) => new(answer.Id, answer.QuestionId, answer.Content, answer.DurationSeconds, ParseJson(answer.Evaluation), answer.CreatedAt);
    private static ReportView MapReport(InterviewReport report) => new(report.Id, report.InterviewSessionId, report.OverallScore,
        ParseJson(report.Rubric) ?? default(JsonElement), ParseJson(report.Strengths) ?? default(JsonElement), ParseJson(report.Gaps) ?? default(JsonElement), ParseJson(report.ActionPlan) ?? default(JsonElement), report.Disclaimer, report.CreatedAt);
    private static JsonElement? ParseJson(string? value) => value is null ? null : JsonSerializer.Deserialize<JsonElement>(value);

    private static BusinessException Validation(string message, string code = "VALIDATION_ERROR") => new(code, message, BusinessErrorKind.Validation);
    private static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    private static BusinessException Conflict(string code, string message) => new(code, message, BusinessErrorKind.Conflict);
    private static BusinessException InvalidState() => Conflict("INVALID_INTERVIEW_STATE", "Trạng thái interview không hợp lệ cho thao tác này.");
    private static BusinessException InvalidAiOutput() => new("AI_OUTPUT_INVALID", "AI trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);
    private static BusinessException AiUnavailable(AiProviderException exception) => new(
        exception.Kind == AiProviderFailureKind.RateLimited ? "AI_RATE_LIMITED" : "AI_PROVIDER_UNAVAILABLE",
        "Dịch vụ AI tạm thời chưa sẵn sàng. Vui lòng thử lại sau.",
        BusinessErrorKind.ExternalFailure);
}
