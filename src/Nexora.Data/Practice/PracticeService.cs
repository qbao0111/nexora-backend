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
    IDetailedDocumentExtractor detailedDocumentExtractor,
    IDocumentOcrProvider documentOcrProvider,
    IResumeContextBuilder resumeContextBuilder,
    IAiProvider aiProvider,
    IStructuredAiExecutor structuredAiExecutor,
    IBillingService billingService,
    IFeatureEntitlementService featureEntitlementService,
    TimeProvider timeProvider,
    ILogger<PracticeService> logger) : IPracticeService, IPracticeJobProcessor
{
    private const string DevelopmentResumeAnalysisOperation = "development-resume-analysis.create";
    private const string JobDescriptionCreateOperation = "job-description.create";
    private const string PromptVersion = "phase3-v1";
    private const string SchemaVersion = "phase3-star-v2";
    private static string ProfilePromptVersion => AiOperations.ResumeProfile.PromptVersion;
    private static string ProfileSchemaVersion => AiOperations.ResumeProfile.SchemaVersion;
    private const string RubricVersion = "interview-rubric-star-v2";
    private const string Disclaimer = "Điểm số chỉ là ước lượng phục vụ coaching, không phải đánh giá tuyển dụng.";
    private const int MinimumReportAnswers = 2;
    private const string ResumeExtractionFailureMessage = "Không thể đọc nội dung CV. Vui lòng thử lại với file PDF hoặc DOCX rõ hơn.";
    private static readonly JsonDocument EmptySchema = JsonDocument.Parse("{}");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonDocument QuestionSchema = JsonDocument.Parse("""{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""");
    private static readonly JsonDocument AnalysisSchema = JsonDocument.Parse("""{"type":"object","properties":{"strengths":{"type":"array","items":{"type":"string"}},"gaps":{"type":"array","items":{"type":"string"}},"recommendations":{"type":"array","items":{"type":"string"}}},"required":["strengths","gaps","recommendations"]}""");
    private static readonly JsonDocument ProfileSchema = JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string"},"skills":{"type":"array","items":{"type":"string"}},"experiences":{"type":"array","items":{"type":"object","properties":{"company":{"type":"string"},"role":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},"highlights":{"type":"array","items":{"type":"string"}}},"required":["company","role","start","end","highlights"]}},"education":{"type":"array","items":{"type":"object","properties":{"institution":{"type":"string"},"degree":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},"details":{"type":"array","items":{"type":"string"}}},"required":["institution","degree","start","end","details"]}},"projects":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"role":{"type":"string"},"technologies":{"type":"array","items":{"type":"string"}},"highlights":{"type":"array","items":{"type":"string"}}},"required":["name","role","technologies","highlights"]}},"certifications":{"type":"array","items":{"type":"string"}},"languages":{"type":"array","items":{"type":"string"}}},"required":["summary","skills","experiences","education","projects","certifications","languages"]}""");
    private static readonly JsonDocument EvaluationSchema = JsonDocument.Parse("""{"type":"object","properties":{"scores":{"type":"array","items":{"type":"object","properties":{"criterion":{"type":"string"},"score":{"type":"integer"},"evidence":{"type":"string"}},"required":["criterion","score","evidence"]}},"feedback":{"type":"string"},"star":{"type":"object","properties":{"applicable":{"type":"boolean"},"overallScore":{"type":"integer"},"situation":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"task":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"action":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"result":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"missingElements":{"type":"array","items":{"type":"string"}},"strengths":{"type":"array","items":{"type":"string"}},"coachingTips":{"type":"array","items":{"type":"string"}}},"required":["applicable"]}},"required":["scores","feedback","star"]}""");
    private static readonly JsonDocument ReportSchema = JsonDocument.Parse("""{"type":"object","properties":{"scores":{"type":"array","items":{"type":"object","properties":{"criterion":{"type":"string"},"score":{"type":"integer"},"evidence":{"type":"string"}},"required":["criterion","score","evidence"]}},"strengths":{"type":"array","items":{"type":"string"}},"gaps":{"type":"array","items":{"type":"string"}},"actionPlan":{"type":"array","items":{"type":"string"}}},"required":["scores","strengths","gaps","actionPlan"]}""");
    private string CurrentModelVersion => string.IsNullOrWhiteSpace(aiProvider.ModelVersion)
        ? throw new InvalidOperationException("The configured AI provider must expose a model version.")
        : aiProvider.ModelVersion.Trim();

    public async Task<DevelopmentResumeAnalysisView> CreateDevelopmentResumeAnalysisAsync(
        Guid userId, Stream content, string fileName, string contentType, long size, string jobDescription, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var normalizedJobDescription = jobDescription?.Trim() ?? string.Empty;
        if (normalizedJobDescription.Length is 0 or > 30_000)
            throw Validation("Job description không hợp lệ.");

        var intent = await uploadProvider.CreateIntentAsync(userId, fileName, contentType, size, cancellationToken);
        await using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, cancellationToken);
        if (buffered.Length != size)
            throw Validation("File không hợp lệ hoặc kích thước không khớp.", "INVALID_FILE");
        var checksum = Convert.ToHexString(SHA256.HashData(buffered.GetBuffer().AsSpan(0, checked((int)buffered.Length)))).ToLowerInvariant();
        var fingerprint = Fingerprint(Path.GetFileName(fileName), contentType.Trim().ToLowerInvariant(), size, checksum, normalizedJobDescription);
        var prior = await FindIdempotentAsync(userId, DevelopmentResumeAnalysisOperation, key, fingerprint, cancellationToken);
        if (prior is not null) return await GetDevelopmentResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);

        buffered.Position = 0;
        await uploadProvider.UploadAsync(intent.Token, buffered, cancellationToken);
        var resume = await CreateResumeAsync(userId, intent.Token, cancellationToken);
        await WaitForResumeReadyAsync(resume.Id, cancellationToken);
        var jd = await CreateJobDescriptionAsync(userId, "Development debug JD", normalizedJobDescription, cancellationToken);
        var analysis = await StartResumeAnalysisAsync(
            userId,
            new StartResumeAnalysisCommand(
                resume.Id,
                ResumeAnalysisModes.JobTargeted,
                jd.Id,
                null,
                null,
                null),
            $"development:{Guid.NewGuid():N}",
            cancellationToken);
        dbContext.IdempotencyRecords.Add(Idempotency(userId, DevelopmentResumeAnalysisOperation, key, fingerprint, analysis.Id, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetDevelopmentResumeAnalysisAsync(userId, analysis.Id, cancellationToken);
    }

    public async Task<ResumeView> CreateResumeAsync(Guid userId, string uploadToken, CancellationToken cancellationToken)
    {
        var upload = await uploadProvider.GetCompletedAsync(userId, uploadToken, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
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
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapResume(resume);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var concurrent = await dbContext.StoredFiles.AsNoTracking()
                .SingleOrDefaultAsync(file => file.StorageKey == upload.StorageKey && file.UserId == userId, cancellationToken);
            if (concurrent is null) throw;
            var existing = await dbContext.Resumes.AsNoTracking().Include(item => item.StoredFile)
                .SingleOrDefaultAsync(item => item.StoredFileId == concurrent.Id && item.UserId == userId, cancellationToken);
            if (existing is null) throw;
            return MapResume(existing);
        }
    }

    public async Task<ResumeView> GetResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken)
    {
        var resume = await dbContext.Resumes.AsNoTracking().Include(item => item.StoredFile)
            .SingleOrDefaultAsync(item => item.Id == resumeId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        return MapResume(resume);
    }

    private async Task WaitForResumeReadyAsync(Guid resumeId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var status = await dbContext.Resumes.AsNoTracking().Where(item => item.Id == resumeId).Select(item => item.Status)
                .SingleAsync(cancellationToken);
            if (status == PracticeValues.Ready) return;
            if (status == PracticeValues.Failed)
                throw Conflict("RESUME_EXTRACTION_FAILED", ResumeExtractionFailureMessage);

            await ProcessPendingAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        throw Conflict("RESUME_NOT_READY", "CV chưa sẵn sàng để phân tích.");
    }

    private async Task<DevelopmentResumeAnalysisView> GetDevelopmentResumeAnalysisAsync(Guid userId, Guid analysisId, CancellationToken cancellationToken)
    {
        var analysis = await dbContext.ResumeAnalyses.AsNoTracking()
            .Include(item => item.Resume).ThenInclude(item => item.StoredFile)
            .Include(item => item.JobDescription)
            .SingleOrDefaultAsync(item => item.Id == analysisId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        return new(MapResume(analysis.Resume), MapJobDescription(analysis.JobDescription ?? throw Validation("JobDescription is required for development analysis.", "RESUME_ANALYSIS_CONTEXT_INVALID")), MapAnalysis(analysis));
    }

    public async Task<JobDescriptionView> CreateJobDescriptionAsync(
        Guid userId,
        string title,
        string content,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 160 || string.IsNullOrWhiteSpace(content) || content.Trim().Length > 30_000)
            throw Validation("Job description không hợp lệ.");
        var normalizedTitle = title.Trim();
        var normalizedContent = content.Trim();
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : RequireKey(idempotencyKey);
        var fingerprint = key is null ? null : Fingerprint(normalizedTitle, normalizedContent);
        if (key is not null)
        {
            var prior = await FindIdempotentAsync(userId, JobDescriptionCreateOperation, key, fingerprint!, cancellationToken);
            if (prior is not null) return await GetJobDescriptionAsync(userId, prior.ResourceId, cancellationToken);
        }

        await using var transaction = key is null ? null : await BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var jobDescription = new JobDescription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = normalizedTitle,
            Content = normalizedContent,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.JobDescriptions.Add(jobDescription);
        if (key is not null)
            dbContext.IdempotencyRecords.Add(Idempotency(userId, JobDescriptionCreateOperation, key, fingerprint!, jobDescription.Id, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapJobDescription(jobDescription);
        }
        catch (DbUpdateException) when (key is not null)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var prior = await FindIdempotentAsync(userId, JobDescriptionCreateOperation, key, fingerprint!, cancellationToken);
                if (prior is not null) return await GetJobDescriptionAsync(userId, prior.ResourceId, cancellationToken);
                await Task.Delay(25, cancellationToken);
            }
            throw;
        }
    }

    private async Task<JobDescriptionView> GetJobDescriptionAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var jobDescription = await dbContext.JobDescriptions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        return MapJobDescription(jobDescription);
    }

    public async Task<ResumeAnalysisView> StartResumeAnalysisAsync(
        Guid userId,
        StartResumeAnalysisCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = NormalizeAnalysisCommand(command);
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(
            command.ResumeId,
            normalized.ModeWire,
            normalized.JobDescriptionId,
            normalized.Industry,
            normalized.TargetRole,
            normalized.Seniority);
        var prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);
        var resume = await dbContext.Resumes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == command.ResumeId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        if (resume.Status != PracticeValues.Ready) throw Conflict("RESUME_NOT_READY", "CV chưa sẵn sàng để phân tích.");
        var jd = normalized.JobDescriptionId is null
            ? null
            : await dbContext.JobDescriptions.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == normalized.JobDescriptionId && item.UserId == userId,
                cancellationToken) ?? throw NotFound();
        await featureEntitlementService.RequireEnabledAsync(userId, FeatureValues.CvAnalysis, cancellationToken);
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.CvAnalysis, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var operation = GetResumeAnalysisOperation(normalized.Mode);
        var analysis = new ResumeAnalysis
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ResumeId = resume.Id,
            JobDescriptionId = jd?.Id,
            ResumeVersion = resume.Version,
            JobDescriptionVersion = jd?.Version,
            Mode = normalized.ModeWire,
            ContextJson = JsonSerializer.Serialize(
                new ResumeAnalysisContextView(
                    normalized.ModeWire,
                    normalized.Industry,
                    normalized.TargetRole,
                    normalized.Seniority),
                JsonOptions),
            Status = PracticeValues.Queued,
            ModelVersion = CurrentModelVersion,
            PromptVersion = operation.PromptVersion,
            RubricVersion = operation.RubricVersion,
            SchemaVersion = operation.SchemaVersion,
            ProfileModelVersion = resume.ProfileModelVersion,
            ProfilePromptVersion = resume.ProfilePromptVersion,
            ProfileSchemaVersion = resume.ProfileSchemaVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
        Guid? reservationId = null;
        if (access.Limit is not null)
        {
            try
            {
                var reservation = await featureEntitlementService.ReserveAsync(userId, FeatureValues.CvAnalysis, analysis.Id.ToString("N"),
                    $"cv-analysis:{key}", cancellationToken);
                reservationId = reservation.EventId;
                analysis.UsageReservationId = reservationId;
            }
            catch (BusinessException ex) when (ex.Code == "IDEMPOTENCY_CONFLICT")
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                for (var i = 0; i < 10; i++)
                {
                    prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
                    if (prior is not null) break;
                    await Task.Delay(25, cancellationToken);
                }
                if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);
                throw;
            }
        }
        dbContext.AddRange(analysis, Idempotency(userId, "resume-analysis.create", key, fingerprint, analysis.Id, now),
            Outbox("ResumeAnalysisRequested", "resume_analysis", analysis.Id, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapAnalysis(analysis);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            for (var i = 0; i < 10; i++)
            {
                prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
                if (prior is not null) break;
                await Task.Delay(25, cancellationToken);
            }
            if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);
            throw;
        }
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
        await CommitAsync(transaction, cancellationToken);
        return MapInterview(session, [], []);
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
        return MapInterview(session, session.Questions, session.Answers, continuation);
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
            .Include(item => item.Resume).Include(item => item.JobDescription)
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken) ?? throw NotFound();
        ValidateQuestionContracts(snapshot.Questions);
        if (snapshot.Status != PracticeValues.Active) throw InvalidState();
        var question = snapshot.Questions.SingleOrDefault(item => item.Id == questionId) ?? throw NotFound();
        if (snapshot.Answers.Any(item => item.QuestionId == questionId)) throw Conflict("ANSWER_ALREADY_EXISTS", "Câu hỏi đã có câu trả lời chính thức.");
        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);

        var isFollowUp = string.Equals(question.Kind, InterviewQuestionValues.Followup, StringComparison.Ordinal);
        string[]? previousMissingElements = null;
        if (isFollowUp)
        {
            var parent = snapshot.Questions.Single(item => item.Id == question.ParentQuestionId);
            var previousAnswer = snapshot.Answers.SingleOrDefault(a => a.QuestionId == parent.Id);

            if (previousAnswer?.Evaluation is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(previousAnswer.Evaluation);
                    if (doc.RootElement.TryGetProperty("star", out var starProp) &&
                        starProp.TryGetProperty("missingElements", out var missingProp) &&
                        missingProp.ValueKind == JsonValueKind.Array)
                    {
                        previousMissingElements = missingProp.EnumerateArray()
                            .Select(e => e.GetString())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s!)
                            .ToArray();
                    }
                }
                catch
                {
                    // safe fallback
                }
            }
        }

        AnswerEvaluation evaluation;
        AiExecutionResult<GeneratedQuestion>? generated = null;
        var profile = TryReadResumeProfile(snapshot.Resume?.StructuredProfile);
        var answerContext = resumeContextBuilder.BuildAnswerEvaluationContext(
            snapshot.Role,
            snapshot.Seniority,
            snapshot.InterviewType,
            snapshot.JobDescription?.Content,
            question.Content,
            content.Trim(),
            profile,
            question.Sequence,
            isFollowUp,
            previousMissingElements,
            question.Topic);
        try
        {
            // Question topic is server-owned semantic metadata. Do not infer STAR
            // applicability from interview type or generated prose: the first
            // primary question is self-introduction even for a behavioral session,
            // while the canonical second primary owns the STAR rubric.
            var isBehavioral = string.Equals(question.Topic, InterviewQuestionValues.BehavioralStar, StringComparison.Ordinal) ||
                string.Equals(question.Topic, InterviewQuestionValues.Behavioral, StringComparison.Ordinal);
            var metadata = new Dictionary<string, string>
            {
                ["questionSequence"] = question.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isFollowup"] = isFollowUp ? "true" : "false",
                ["questionTopic"] = question.Topic
            };
            if (previousMissingElements is not null && previousMissingElements.Length > 0)
            {
                metadata["followupTargetElements"] = string.Join(",", previousMissingElements);
            }
            var evalResult = await structuredAiExecutor.ExecuteAsync(
                AiOperations.InterviewEvaluate,
                answerContext,
                new AiOperationContext(
                    interviewId.ToString("N"),
                    userId,
                    ExpectedStar: isBehavioral,
                    Metadata: metadata,
                    CandidateAnswer: content.Trim()),
                cancellationToken);
            evaluation = evalResult.Value;

            var hasNextQuestion = snapshot.Questions.Any(item => item.Sequence > question.Sequence);
            if (!isFollowUp && question.Sequence < 3 && !hasNextQuestion && snapshot.Questions.Count < questionLimit)
            {
                var nextSequence = question.Sequence + 1;
                var nextTopic = InterviewQuestionValues.PrimaryTopicForSequence(nextSequence);
                var nextContext = resumeContextBuilder.BuildInterviewQuestionContext(
                    snapshot.Role,
                    snapshot.Seniority,
                    snapshot.InterviewType,
                    snapshot.Difficulty,
                    snapshot.JobDescription?.Content,
                    profile,
                    nextSequence,
                    nextTopic);
                var nextMetadata = new Dictionary<string, string>
                {
                    ["questionSequence"] = nextSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["questionTopic"] = nextTopic,
                    ["questionKind"] = InterviewQuestionValues.Primary
                };
                var nextResult = await structuredAiExecutor.ExecuteAsync(
                    AiOperations.InterviewFirstQuestion,
                    nextContext,
                    new AiOperationContext(interviewId.ToString("N"), userId, Metadata: nextMetadata),
                    cancellationToken);
                generated = nextResult;
            }
        }
        catch (AiProviderException exception)
        {
            throw AiUnavailable(exception);
        }

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
        if (generated is not null && !string.IsNullOrWhiteSpace(generated.Value.Content))
        {
            nextQuestion = new InterviewQuestion
            {
                Id = Guid.NewGuid(),
                InterviewSessionId = session.Id,
                Sequence = session.Questions.Count + 1,
                Kind = InterviewQuestionValues.Primary,
                Topic = InterviewQuestionValues.PrimaryTopicForSequence(question.Sequence + 1),
                ParentQuestionId = null,
                Content = generated.Value.Content.Trim()[..Math.Min(generated.Value.Content.Trim().Length, 2_000)],
                PromptVersion = generated.PromptVersion,
                ModelVersion = generated.ModelVersion,
                CreatedAt = now
            };
            dbContext.InterviewQuestions.Add(nextQuestion);
            session.Questions.Add(nextQuestion);
        }
        session.Answers.Add(answer);
        nextQuestion ??= session.Questions
            .OrderBy(item => item.Sequence)
            .FirstOrDefault(item => session.Answers.All(existing => existing.QuestionId != item.Id));
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(answer, Idempotency(userId, "interview.answer", key, fingerprint, answer.Id, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            prior = await FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
            if (prior is not null) return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);
            throw Conflict("CONCURRENT_SUBMISSION", "Câu trả lời đang được xử lý hoặc đã được nộp.");
        }
        var continuation = await BuildContinuationAsync(userId, session, cancellationToken);
        var isComplete = nextQuestion is null && continuation?.State == InterviewContinuationValues.MaxQuestionsReached;
        return new AnswerResult(MapAnswer(answer), nextQuestion is null ? null : MapQuestion(nextQuestion), isComplete, continuation);
    }

    public async Task<InterviewView> ContinueInterviewAsync(
        Guid userId,
        Guid interviewId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Reference(item => item.Resume).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Reference(item => item.JobDescription).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status != PracticeValues.Active) throw InvalidState();
        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);

        if (session.Questions.Count < InterviewQuestionValues.FreeQuestionLimit ||
            session.Answers.Count < InterviewQuestionValues.FreeQuestionLimit)
            throw InvalidState();

        var existingPending = session.Questions
            .OrderBy(item => item.Sequence)
            .FirstOrDefault(item => session.Answers.All(answer => answer.QuestionId != item.Id));
        if (existingPending is not null)
        {
            var replayAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.continue", key, fingerprint, session.Id, replayAt));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapInterview(session, session.Questions, session.Answers, BuildContinuation(session, questionLimit));
        }

        if (session.Questions.Count >= questionLimit)
        {
            if (questionLimit <= InterviewQuestionValues.FreeQuestionLimit)
                throw InterviewUpgradeRequired();
            throw InterviewLimitReached();
        }
        var lastQuestion = session.Questions.OrderBy(item => item.Sequence).Last();
        var lastAnswer = session.Answers.SingleOrDefault(item => item.QuestionId == lastQuestion.Id) ?? throw InvalidState();
        var lastEvaluation = TryDeserializeAnswerEvaluation(lastAnswer.Evaluation);
        var paidTopic = InterviewQuestionValues.PaidTopicForContext(
            session.InterviewType,
            session.Resume is not null,
            session.JobDescription is not null);
        var useStarFollowup =
            (string.Equals(lastQuestion.Topic, InterviewQuestionValues.Behavioral, StringComparison.Ordinal) ||
             string.Equals(lastQuestion.Topic, InterviewQuestionValues.BehavioralStar, StringComparison.Ordinal)) &&
            lastQuestion.Kind == InterviewQuestionValues.Primary &&
            lastEvaluation?.Star is { Applicable: true, MissingElements.Count: > 0 };
        var profile = session.Resume is null ? null : TryReadResumeProfile(session.Resume.StructuredProfile);
        AiExecutionResult<GeneratedQuestion> generated;
        try
        {
            generated = await structuredAiExecutor.ExecuteAsync<GeneratedQuestion>(
                useStarFollowup ? AiOperations.InterviewFollowup : AiOperations.InterviewFirstQuestion,
                useStarFollowup
                    ? resumeContextBuilder.BuildFollowupQuestionContext(
                        session.Role,
                        session.Seniority,
                        session.InterviewType,
                        session.JobDescription?.Content,
                        lastQuestion.Content,
                        lastAnswer.Content,
                        lastEvaluation?.Star,
                        profile)
                    : resumeContextBuilder.BuildInterviewQuestionContext(
                        session.Role,
                        session.Seniority,
                        session.InterviewType,
                        session.Difficulty,
                        session.JobDescription?.Content,
                        profile,
                        session.Questions.Count + 1,
                        paidTopic),
                new AiOperationContext(
                    session.Id.ToString("N"),
                    userId,
                    Metadata: new Dictionary<string, string>
                    {
                        ["questionSequence"] = (session.Questions.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["questionKind"] = useStarFollowup ? InterviewQuestionValues.Followup : InterviewQuestionValues.Primary,
                        ["questionTopic"] = useStarFollowup ? lastQuestion.Topic : paidTopic,
                        ["parentQuestionId"] = useStarFollowup ? lastQuestion.Id.ToString("D") : string.Empty
                    }),
                cancellationToken);
        }
        catch (AiProviderException exception)
        {
            throw AiUnavailable(exception);
        }
        if (string.IsNullOrWhiteSpace(generated.Value.Content))
            throw InvalidAiOutput();

        var now = timeProvider.GetUtcNow();
        var nextQuestion = new InterviewQuestion
        {
            Id = Guid.NewGuid(),
            InterviewSessionId = session.Id,
            Sequence = session.Questions.Count + 1,
            Kind = useStarFollowup ? InterviewQuestionValues.Followup : InterviewQuestionValues.Primary,
            Topic = useStarFollowup ? lastQuestion.Topic : paidTopic,
            ParentQuestionId = useStarFollowup ? lastQuestion.Id : null,
            Content = generated.Value.Content.Trim()[..Math.Min(generated.Value.Content.Trim().Length, 2_000)],
            PromptVersion = generated.PromptVersion,
            ModelVersion = generated.ModelVersion,
            CreatedAt = now
        };
        dbContext.InterviewQuestions.Add(nextQuestion);
        session.Version++;
        session.UpdatedAt = now;
        dbContext.Add(Idempotency(userId, "interview.continue", key, fingerprint, session.Id, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            prior = await FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
            if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
            var replay = await GetInterviewAsync(userId, interviewId, cancellationToken);
            if (replay.Questions.Any(item => item.Sequence > InterviewQuestionValues.FreeQuestionLimit)) return replay;
            throw;
        }
        return await GetInterviewAsync(userId, interviewId, cancellationToken);
    }

    public async Task<InterviewView> CompleteInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.complete", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        var lockedPrior = await FindIdempotentAsync(userId, "interview.complete", key, fingerprint, cancellationToken);
        if (lockedPrior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, lockedPrior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status == PracticeValues.Completed)
        {
            dbContext.Add(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, timeProvider.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapInterview(session, session.Questions, session.Answers);
        }
        if (session.Status == PracticeValues.Completing)
        {
            var retryAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, retryAt));
            if (!await HasPendingReportJobAsync(session.Id, cancellationToken))
                dbContext.Add(Outbox("InterviewReportRequested", "interview", session.Id, retryAt));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapInterview(session, session.Questions, session.Answers);
        }
        var answeredCount = session.Answers.Count(answer => !string.IsNullOrWhiteSpace(answer.Content));
        if (session.Status != PracticeValues.Active || session.Questions.Count == 0 ||
            answeredCount < MinimumReportAnswers || answeredCount != session.Answers.Count)
            throw InvalidState();
        var now = timeProvider.GetUtcNow();
        session.Status = PracticeValues.Completing;
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, now),
            Outbox("InterviewReportRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return MapInterview(session, session.Questions, session.Answers);
    }

    public async Task<InterviewView> RetryReportAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.report.retry", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        var lockedPrior = await FindIdempotentAsync(userId, "interview.report.retry", key, fingerprint, cancellationToken);
        if (lockedPrior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, lockedPrior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status is not PracticeValues.Completing and not PracticeValues.Completed)
            throw InvalidState();

        var now = timeProvider.GetUtcNow();
        dbContext.Add(Idempotency(userId, "interview.report.retry", key, fingerprint, session.Id, now));
        if (session.Status == PracticeValues.Completing && !await HasPendingReportJobAsync(session.Id, cancellationToken))
            dbContext.Add(Outbox("InterviewReportRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return MapInterview(session, session.Questions, session.Answers);
    }

    public async Task<ReportView> GetReportAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken)
    {
        var report = await dbContext.InterviewReports.AsNoTracking()
            .Include(item => item.InterviewSession).ThenInclude(item => item.Answers)
            .Include(item => item.InterviewSession).ThenInclude(item => item.Questions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.InterviewSessionId == interviewId && item.UserId == userId, cancellationToken);
        if (report is not null)
            return MapReport(report, report.InterviewSession.Questions, report.InterviewSession.Answers);

        var session = await dbContext.InterviewSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        if (session.Status == PracticeValues.Completing)
        {
            var latestJob = (await dbContext.OutboxEvents.AsNoTracking()
                    .Where(item => item.AggregateId == interviewId && item.Type == "InterviewReportRequested")
                    .ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
            if (latestJob?.Status == BillingValues.Failed)
                throw Conflict("INTERVIEW_REPORT_FAILED", "Báo cáo phỏng vấn chưa tạo được. Bạn có thể thử lại.");

            throw Conflict("INTERVIEW_REPORT_PROCESSING", "Báo cáo phỏng vấn đang được xử lý.");
        }

        throw Conflict("INTERVIEW_REPORT_UNAVAILABLE", "Báo cáo phỏng vấn chưa sẵn sàng.");
    }

    public async Task<DashboardView> GetDashboardAsync(Guid userId, CancellationToken cancellationToken)
    {
        var billing = await billingService.GetSummaryAsync(userId, cancellationToken);
        var isSqlite = string.Equals(
            dbContext.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.Sqlite",
            StringComparison.Ordinal);
        var interviewRowsQuery = dbContext.InterviewSessions.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.Id, item.Role, item.Status, item.UpdatedAt });
        var interviewRows = isSqlite
            ? await dbContext.InterviewSessions
                .FromSqlInterpolated($"SELECT * FROM interview_sessions WHERE \"UserId\" = {userId} ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT 20")
                .AsNoTracking()
                .Select(item => new { item.Id, item.Role, item.Status, item.UpdatedAt })
                .ToArrayAsync(cancellationToken)
            : await interviewRowsQuery
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.Id)
                .Take(20)
                .ToArrayAsync(cancellationToken);
        var interviews = interviewRows
            .Select(item => new InterviewSummary(item.Id, item.Role, item.Status, item.UpdatedAt))
            .ToArray();

        var reportRowsQuery = dbContext.InterviewReports.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt });
        var reportRows = isSqlite
            ? await dbContext.InterviewReports
                .FromSqlInterpolated($"SELECT * FROM interview_reports WHERE \"UserId\" = {userId} ORDER BY \"CreatedAt\" DESC, \"Id\" DESC LIMIT 20")
                .AsNoTracking()
                .Select(item => new { item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt })
                .ToArrayAsync(cancellationToken)
            : await reportRowsQuery
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Take(20)
                .ToArrayAsync(cancellationToken);
        var reports = reportRows
            .Select(item => new ReportSummary(item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt))
            .ToArray();
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
        resume.Status = PracticeValues.Extracting;
        resume.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        await using var source = await storageProvider.OpenReadAsync(resume.StoredFile.StorageKey, cancellationToken);
        await using var buffered = await ReadStoredFileBoundedAsync(source, resume.StoredFile.Size, cancellationToken);
        var actualChecksum = buffered.Length == resume.StoredFile.Size
            ? Convert.ToHexString(SHA256.HashData(buffered.GetBuffer().AsSpan(0, checked((int)buffered.Length)))).ToLowerInvariant()
            : string.Empty;
        if (buffered.Length != resume.StoredFile.Size ||
            !string.Equals(actualChecksum, resume.StoredFile.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            resume.Status = PracticeValues.Failed;
            resume.UpdatedAt = timeProvider.GetUtcNow();
            EnqueueResourceChanged(resume.UserId, "resume", resume.Id, resume.Status, resume.UpdatedAt);
            MarkProcessed(job);
            StorageIntegrityFailed(logger, resume.Id, buffered.Length, resume.StoredFile.Size);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }
        buffered.Position = 0;

        DocumentExtractionResult? localExtraction = null;
        try
        {
            localExtraction = await detailedDocumentExtractor.ExtractDetailedAsync(
                buffered, resume.StoredFile.ContentType, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            LocalExtractionFailed(logger, resume.Id, exception.GetType().Name);
        }

        if (localExtraction?.Quality == DocumentExtractionQuality.Good)
        {
            await CompleteResumeExtractionAsync(resume, job, localExtraction, profile: null, ocrFallbackUsed: false, cancellationToken: cancellationToken);
            return;
        }

        resume.Status = PracticeValues.OcrFallback;
        resume.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        OcrFallbackStarted(logger, resume.Id, localExtraction?.Quality.ToString() ?? DocumentExtractionQuality.Failed.ToString());

        buffered.Position = 0;
        var fallback = await documentOcrProvider.ExtractAsync(buffered, resume.StoredFile.ContentType, cancellationToken);
        var fallbackPageCount = fallback.PageCount > 0 ? fallback.PageCount : localExtraction?.PageCount ?? 1;
        var extraction = detailedDocumentExtractor.EvaluateExtractedText(
            fallback.ExtractedText,
            fallbackPageCount,
            DocumentExtractionMethod.GeminiOcr,
            fallback.Warnings.Append("OCR_FALLBACK_USED"));
        if (extraction.Quality != DocumentExtractionQuality.Good)
            throw new InvalidDataException("Gemini document extraction did not produce usable text.");

        ValidateResumeProfile(fallback.Profile);
        await CompleteResumeExtractionAsync(resume, job, extraction, fallback.Profile, ocrFallbackUsed: true, cancellationToken: cancellationToken);
    }

    private static async Task<MemoryStream> ReadStoredFileBoundedAsync(
        Stream source,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 80 * 1024;
        const long maximumStoredFileSize = 25 * 1024 * 1024;
        if (expectedSize is < 0 or > maximumStoredFileSize)
            return new MemoryStream();

        var buffered = new MemoryStream(capacity: checked((int)expectedSize));
        var buffer = new byte[bufferSize];
        var bytesRead = 0L;
        while (bytesRead <= expectedSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = expectedSize + 1 - bytesRead;
            var readLength = (int)Math.Min(buffer.Length, remaining);
            if (readLength <= 0) break;
            var read = await source.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken);
            if (read == 0) break;
            bytesRead += read;
            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (bytesRead > expectedSize) break;
        }

        return buffered;
    }

    private async Task CompleteResumeExtractionAsync(
        ResumeRecord resume,
        OutboxEvent job,
        DocumentExtractionResult extraction,
        ResumeProfile? profile,
        bool ocrFallbackUsed,
        CancellationToken cancellationToken)
    {
        resume.ExtractedText = extraction.Text;
        if (profile is not null)
        {
            resume.StructuredProfile = JsonSerializer.Serialize(profile, JsonOptions);
            if (ocrFallbackUsed)
            {
                // OCR output is produced by a separate document-understanding
                // provider and is not a text-profile execution of the current
                // ResumeProfile operation. Force the canonical profile provider
                // to regenerate it before any analysis uses the cache.
                resume.ProfileModelVersion = null;
                resume.ProfilePromptVersion = null;
                resume.ProfileSchemaVersion = null;
            }
            else
            {
                resume.ProfileModelVersion = CurrentModelVersion;
                resume.ProfilePromptVersion = ProfilePromptVersion;
                resume.ProfileSchemaVersion = ProfileSchemaVersion;
            }
        }
        resume.Status = PracticeValues.Ready;
        resume.UpdatedAt = timeProvider.GetUtcNow();
        EnqueueResourceChanged(resume.UserId, "resume", resume.Id, resume.Status, resume.UpdatedAt);
        ResumeExtractionMeasured(logger, resume.Id, extraction.PageCount, extraction.CharacterCount, extraction.WordCount,
            extraction.ExtractionMethod.ToString(), extraction.QualityScore, string.Join(',', extraction.Warnings), ocrFallbackUsed);
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ResumeProfile> EnsureResumeProfileAsync(
        ResumeRecord resume, Guid correlationId, CancellationToken cancellationToken)
    {
        if (resume.ProfilePromptVersion == ProfilePromptVersion &&
            resume.ProfileSchemaVersion == ProfileSchemaVersion &&
            resume.ProfileModelVersion == CurrentModelVersion)
        {
            var existing = TryReadResumeProfile(resume.StructuredProfile);
            if (existing is not null) return existing;
        }

        if (string.IsNullOrWhiteSpace(resume.ExtractedText)) throw InvalidAiOutput();
        var context = resumeContextBuilder.BuildProfileExtractionContext(resume.ExtractedText);
        ResumeProfile profile;
        try
        {
            var execResult = await structuredAiExecutor.ExecuteAsync(
                AiOperations.ResumeProfile,
                context,
                new AiOperationContext(correlationId.ToString("N")),
                cancellationToken);
            profile = execResult.Value;
        }
        catch (AiProviderException exception)
        {
            throw AiUnavailable(exception);
        }

        resume.StructuredProfile = JsonSerializer.Serialize(profile, JsonOptions);
        resume.ProfileModelVersion = CurrentModelVersion;
        resume.ProfilePromptVersion = ProfilePromptVersion;
        resume.ProfileSchemaVersion = ProfileSchemaVersion;
        resume.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return profile;
    }

    private static ResumeProfile? TryReadResumeProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var validation = ResumeProfileValidator.NormalizeAndValidate(JsonSerializer.Deserialize<ResumeProfile>(value, JsonOptions));
            return validation.IsValid ? validation.NormalizedValue : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateResumeProfile(ResumeProfile profile)
    {
        if (!ResumeProfileValidator.NormalizeAndValidate(profile).IsValid) throw InvalidAiOutput();
    }

    private async Task AnalyzeResumeAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var analysis = await dbContext.ResumeAnalyses.Include(item => item.Resume).Include(item => item.JobDescription)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        analysis.Status = PracticeValues.Processing;
        analysis.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        var profile = await EnsureResumeProfileAsync(analysis.Resume, analysis.Id, cancellationToken);
        analysis.ProfileSnapshot = JsonSerializer.Serialize(profile, JsonOptions);
        analysis.ProfileModelVersion = analysis.Resume.ProfileModelVersion;
        analysis.ProfilePromptVersion = analysis.Resume.ProfilePromptVersion;
        analysis.ProfileSchemaVersion = analysis.Resume.ProfileSchemaVersion;
        await dbContext.SaveChangesAsync(cancellationToken);

        var contextSnapshot = ReadAnalysisContext(analysis);
        if (!ResumeAnalysisModes.TryParse(contextSnapshot.Mode, out var analysisMode))
            throw Validation("Mode phân tích CV không hợp lệ.", "RESUME_ANALYSIS_MODE_INVALID");
        var input = resumeContextBuilder.BuildResumeAnalysisContext(profile, new ResumeAnalysisContext(
            analysisMode,
            analysis.JobDescription?.Content,
            contextSnapshot.Industry,
            contextSnapshot.TargetRole,
            contextSnapshot.Seniority));
        var operation = GetResumeAnalysisOperation(analysisMode);
        var execResult = await structuredAiExecutor.ExecuteAsync(
            operation,
            input,
            new AiOperationContext(
                analysis.Id.ToString("N"),
                analysis.UserId,
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ResumeAnalysisMetadata.Mode] = analysisMode.ToWireValue()
                }),
            cancellationToken);
        var result = execResult.Value;

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        analysis.Result = JsonSerializer.Serialize(result, JsonOptions);
        analysis.ModelVersion = execResult.ModelVersion;
        analysis.PromptVersion = execResult.PromptVersion;
        analysis.RubricVersion = execResult.RubricVersion;
        analysis.SchemaVersion = execResult.SchemaVersion;
        analysis.Status = PracticeValues.Completed;
        analysis.CompletedAt = analysis.UpdatedAt = timeProvider.GetUtcNow();
        EnqueueResourceChanged(analysis.UserId, "resumeAnalysis", analysis.Id, analysis.Status, analysis.UpdatedAt);
        MarkProcessed(job);
        if (analysis.UsageReservationId.HasValue)
            await featureEntitlementService.ConsumeAsync(analysis.UserId, analysis.UsageReservationId.Value, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task ActivateInterviewAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.Include(item => item.Resume).Include(item => item.JobDescription)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (snapshot.Status != PracticeValues.Starting) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); return; }
        var profile = snapshot.Resume is null ? null : await EnsureResumeProfileAsync(snapshot.Resume, snapshot.Id, cancellationToken);
        var firstTopic = InterviewQuestionValues.PrimaryTopicForSequence(1);
        var context = resumeContextBuilder.BuildInterviewQuestionContext(
            snapshot.Role,
            snapshot.Seniority,
            snapshot.InterviewType,
            snapshot.Difficulty,
            snapshot.JobDescription?.Content,
            profile,
            1,
            firstTopic);
        var execResult = await structuredAiExecutor.ExecuteAsync(
            AiOperations.InterviewFirstQuestion,
            context,
            new AiOperationContext(
                snapshot.Id.ToString("N"),
                snapshot.UserId,
                Metadata: new Dictionary<string, string>
                {
                    ["questionSequence"] = "1",
                    ["questionKind"] = InterviewQuestionValues.Primary,
                    ["questionTopic"] = firstTopic
                }),
            cancellationToken);
        var generated = execResult.Value;

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (session.Status != PracticeValues.Starting) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); await CommitAsync(transaction, cancellationToken); return; }
        var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
        var entitlement = await FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
        var now = timeProvider.GetUtcNow();
        dbContext.InterviewQuestions.Add(new InterviewQuestion
        {
            Id = Guid.NewGuid(),
            InterviewSessionId = session.Id,
            Sequence = 1,
            Kind = InterviewQuestionValues.Primary,
            Topic = firstTopic,
            Content = generated.Content.Trim()[..Math.Min(generated.Content.Trim().Length, 2_000)],
            PromptVersion = execResult.PromptVersion,
            ModelVersion = execResult.ModelVersion,
            CreatedAt = now
        });
        FinalizeReservation(entitlement, reservation, BillingValues.Consume, now);
        session.Status = PracticeValues.Active;
        EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
        session.Version++;
        session.UpdatedAt = now;
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task BuildReportAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.AsNoTracking().Include(item => item.Questions).ThenInclude(question => question.Answer)
            .Include(item => item.Resume).Include(item => item.JobDescription)
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
        var execResult = await structuredAiExecutor.ExecuteAsync(
            AiOperations.InterviewReport,
            reportContext,
            new AiOperationContext(
                snapshot.Id.ToString("N"),
                snapshot.UserId,
                GroundingTranscript: string.Join("\n", answeredQuestions.Select(item => item.Answer!.Content))),
            cancellationToken);
        var output = execResult.Value;
        ValidateScores(output.Scores);
        if (output.Strengths.Count == 0 || output.Gaps.Count == 0 || output.ActionPlan.Count == 0) throw InvalidAiOutput();
        var overall = WeightedScore(output.Scores);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(snapshot.UserId, job.AggregateId, cancellationToken) ?? throw NotFound();
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
        EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
        session.Version++;
        session.UpdatedAt = now;
        session.CompletedAt = now;
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
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
                EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, current.ProcessedAt.Value);
                session.Version++;
                session.UpdatedAt = current.ProcessedAt.Value;
            }
        }
        else if (current.Type == "ResumeExtractionRequested")
        {
            var resume = await dbContext.Resumes.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            resume.Status = PracticeValues.Failed;
            resume.UpdatedAt = current.ProcessedAt.Value;
            EnqueueResourceChanged(resume.UserId, "resume", resume.Id, resume.Status, resume.UpdatedAt);
        }
        else if (current.Type == "ResumeAnalysisRequested")
        {
            var analysis = await dbContext.ResumeAnalyses.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            analysis.Status = PracticeValues.Failed;
            analysis.ErrorCode = "AI_PROCESSING_FAILED";
            analysis.UpdatedAt = current.ProcessedAt.Value;
            EnqueueResourceChanged(analysis.UserId, "resumeAnalysis", analysis.Id, analysis.Status, analysis.UpdatedAt);
            if (analysis.UsageReservationId.HasValue)
                await featureEntitlementService.VoidAsync(analysis.UserId, analysis.UsageReservationId.Value, cancellationToken);
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
        await CommitAsync(transaction, cancellationToken);
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

    private void EnqueueResourceChanged(Guid userId, string resourceType, Guid resourceId, string status, DateTimeOffset occurredAt) =>
        dbContext.RealtimeNotifications.Add(new Nexora.Data.Realtime.RealtimeNotification
        {
            UserId = userId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Status = status,
            CreatedAt = occurredAt
        });

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
        ValidateQuestionContracts(session.Questions);
        var answer = session.Answers.Single(item => item.Id == answerId);
        var next = session.Questions
            .OrderBy(item => item.Sequence)
            .FirstOrDefault(item => session.Answers.All(existing => existing.QuestionId != item.Id));
        var continuation = await BuildContinuationAsync(userId, session, cancellationToken);
        var isComplete = next is null && continuation?.State == InterviewContinuationValues.MaxQuestionsReached;
        return new AnswerResult(MapAnswer(answer), next is null ? null : MapQuestion(next), isComplete, continuation);
    }

    private async Task<IdempotencyRecord?> FindIdempotentAsync(Guid userId, string operation, string key, string fingerprint, CancellationToken cancellationToken)
    {
        var record = await dbContext.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ActorId == userId && item.Operation == operation && item.Key == key, cancellationToken);
        if (record is not null && record.RequestFingerprint != fingerprint)
            throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
        return record;
    }

    private Task<bool> HasPendingReportJobAsync(Guid interviewId, CancellationToken cancellationToken) =>
        dbContext.OutboxEvents.AnyAsync(item => item.AggregateId == interviewId &&
            item.Type == "InterviewReportRequested" &&
            (item.Status == BillingValues.Pending || item.Status == BillingValues.Processing), cancellationToken);

    private async Task<InterviewSession?> FindInterviewForUpdateAsync(
        Guid userId,
        Guid interviewId,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            return await dbContext.InterviewSessions.FromSqlInterpolated(
                    $"SELECT * FROM interview_sessions WHERE \"Id\" = {interviewId} AND \"UserId\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await dbContext.InterviewSessions
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken);
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

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null) return null;
        return await dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
    }

    private static async Task CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is null) return;
        await transaction.CommitAsync(cancellationToken);
    }

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

    private static int WeightedStarScore(StarComponentEvaluation situation, StarComponentEvaluation task, StarComponentEvaluation action, StarComponentEvaluation result) =>
        (int)Math.Round(situation.Score * .20 + task.Score * .20 + action.Score * .35 + result.Score * .25);

    private static StarReportSummary? BuildStarSummary(
        IEnumerable<InterviewQuestion> questions,
        IEnumerable<InterviewAnswer> answers)
    {
        var questionMap = questions.ToDictionary(item => item.Id);
        ValidateQuestionContracts(questionMap.Values);
        var evaluations = new List<PersistedStarEvaluation>();
        foreach (var answer in answers)
        {
            if (TryReadStarEvaluation(answer, questionMap, out var evaluation) && evaluation is not null)
                evaluations.Add(evaluation);
        }

        var stories = evaluations
            .GroupBy(item => item.RootQuestionId)
            .Select(story => BuildStarStory(story, questionMap))
            .OrderByDescending(item => item.Evaluations.Count)
            .ThenBy(item => item.RootSequence)
            .ToArray();
        if (stories.Length == 0) return null;

        var story = stories[0];
        var orderedEvaluations = story.Evaluations.ToArray();

        var situation = MergeStarComponent(orderedEvaluations, star => star.Situation);
        var task = MergeStarComponent(orderedEvaluations, star => star.Task);
        var action = MergeStarComponent(orderedEvaluations, star => star.Action);
        var result = MergeStarComponent(orderedEvaluations, star => star.Result);
        var components = new[]
        {
            (Name: "situation", Component: situation),
            (Name: "task", Component: task),
            (Name: "action", Component: action),
            (Name: "result", Component: result)
        };

        var strongest = components
            .Select((item, index) => (item, index))
            .OrderByDescending(item => item.item.Component.Score)
            .ThenBy(item => item.index)
            .First().item.Name;
        var weakest = components
            .Select((item, index) => (item, index))
            .OrderBy(item => item.item.Component.Score)
            .ThenBy(item => item.index)
            .First().item.Name;

        return new StarReportSummary(
            orderedEvaluations.Length,
            WeightedStarScore(situation, task, action, result),
            new StarComponentAverages(situation.Score, task.Score, action.Score, result.Score),
            strongest,
            weakest,
            components
                .Where(item => !item.Component.Detected || item.Component.Score < 60)
                .Select(item => item.Name)
                .Take(3)
                .ToArray(),
            components
                .Select((item, index) => (item, index))
                .Where(item => !item.item.Component.Detected || item.item.Component.Score < 60)
                .OrderBy(item => item.item.Component.Score)
                .ThenBy(item => item.index)
                .Select(item => item.item.Component.Feedback)
                .Where(NotBlank)
                .Select(item => item!.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToArray());
    }

    private static bool TryReadStarEvaluation(
        InterviewAnswer answer,
        Dictionary<Guid, InterviewQuestion> questions,
        out PersistedStarEvaluation? evaluation)
    {
        evaluation = null;
        if (string.IsNullOrWhiteSpace(answer.Evaluation) || !questions.TryGetValue(answer.QuestionId, out var question))
            return false;

        try
        {
            var star = JsonSerializer.Deserialize<AnswerEvaluation>(answer.Evaluation, JsonOptions)?.Star;
            if (!IsUsableStar(star)) return false;
            evaluation = new PersistedStarEvaluation(
                answer.Id,
                question.Sequence,
                ResolveStoryRoot(question.Id, questions),
                answer.CreatedAt,
                star!);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsUsableStar(StarEvaluation? star) =>
        star is not null && star.Applicable &&
        IsUsableStarComponent(star.Situation) &&
        IsUsableStarComponent(star.Task) &&
        IsUsableStarComponent(star.Action) &&
        IsUsableStarComponent(star.Result);

    private static bool IsUsableStarComponent(StarComponentEvaluation? component)
    {
        if (component is null || component.Score is < 0 or > 100) return false;
        return component.Detected
            ? component.Score > 0 && !string.IsNullOrWhiteSpace(component.Evidence)
            : component.Score == 0 && string.IsNullOrWhiteSpace(component.Evidence);
    }

    private static StarComponentEvaluation MergeStarComponent(
        IReadOnlyCollection<PersistedStarEvaluation> evaluations,
        Func<StarEvaluation, StarComponentEvaluation?> selector)
    {
        var candidates = evaluations
            .Select(item => new { item, component = selector(item.Star) })
            .Where(item => item.component is not null)
            .ToArray();
        var strongestDetected = candidates
            .Where(item => item.component!.Detected && !string.IsNullOrWhiteSpace(item.component.Evidence))
            .OrderByDescending(item => item.component!.Score)
            .ThenByDescending(item => item.item.Sequence)
            .ThenByDescending(item => item.item.CreatedAt)
            .ThenByDescending(item => item.item.AnswerId)
            .Select(item => item.component!)
            .FirstOrDefault();
        if (strongestDetected is not null) return strongestDetected;

        var latestUndetected = candidates
            .OrderByDescending(item => item.item.Sequence)
            .ThenByDescending(item => item.item.CreatedAt)
            .ThenByDescending(item => item.item.AnswerId)
            .Select(item => item.component!)
            .FirstOrDefault();
        return latestUndetected is null
            ? new StarComponentEvaluation(0, false, string.Empty, string.Empty)
            : new StarComponentEvaluation(0, false, string.Empty, latestUndetected.Feedback);
    }

    private static StarStorySummary BuildStarStory(
        IGrouping<Guid, PersistedStarEvaluation> story,
        Dictionary<Guid, InterviewQuestion> questions)
    {
        var evaluations = story
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.AnswerId)
            .ToArray();
        if (!questions.TryGetValue(story.Key, out var rootQuestion) ||
            !string.Equals(rootQuestion.Kind, InterviewQuestionValues.Primary, StringComparison.Ordinal))
            throw InvalidState();
        return new StarStorySummary(story.Key, rootQuestion.Sequence, evaluations);
    }

    private static Guid ResolveStoryRoot(Guid questionId, Dictionary<Guid, InterviewQuestion> questions)
    {
        var visited = new HashSet<Guid>();
        var currentId = questionId;
        while (true)
        {
            if (!visited.Add(currentId) || !questions.TryGetValue(currentId, out var question))
                throw InvalidState();
            if (question.ParentQuestionId is null) return question.Id;
            currentId = question.ParentQuestionId.Value;
        }
    }

    private static void ValidateQuestionContracts(IEnumerable<InterviewQuestion> questions)
    {
        var questionMap = questions.ToDictionary(item => item.Id);
        foreach (var question in questionMap.Values)
        {
            if (!InterviewQuestionValues.IsSupportedKind(question.Kind) ||
                string.IsNullOrWhiteSpace(question.Topic) || question.Topic.Trim().Length > 80)
                throw InvalidState();

            if (string.Equals(question.Kind, InterviewQuestionValues.Primary, StringComparison.Ordinal))
            {
                if (question.ParentQuestionId is not null) throw InvalidState();
                continue;
            }

            if (question.ParentQuestionId is null ||
                !questionMap.TryGetValue(question.ParentQuestionId.Value, out var parent) ||
                parent.Id == question.Id ||
                parent.InterviewSessionId != question.InterviewSessionId ||
                parent.Sequence >= question.Sequence ||
                !string.Equals(parent.Topic, question.Topic, StringComparison.Ordinal))
                throw InvalidState();
        }
    }

    private sealed record PersistedStarEvaluation(
        Guid AnswerId,
        int Sequence,
        Guid RootQuestionId,
        DateTimeOffset CreatedAt,
        StarEvaluation Star);

    private sealed record StarStorySummary(
        Guid RootQuestionId,
        int RootSequence,
        IReadOnlyCollection<PersistedStarEvaluation> Evaluations);

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    private static int WeightedScore(IReadOnlyCollection<RubricScore> scores)
    {
        var values = scores.ToDictionary(item => item.Criterion, item => item.Score, StringComparer.Ordinal);
        return (int)Math.Round(values["correctness"] * .40 + values["structure"] * .25 + values["completeness"] * .20 + values["clarity"] * .15);
    }

    private AiRequest Request(string purpose, string input, Guid correlationId)
    {
        var schema = purpose switch
        {
            "resume.profile" => ProfileSchema,
            "resume.analysis" => AnalysisSchema,
            "interview.evaluate" => EvaluationSchema,
            "interview.report" => ReportSchema,
            "interview.first-question" or "interview.followup" => QuestionSchema,
            _ => EmptySchema
        };
        var isProfile = purpose == "resume.profile";
        var maxOutputTokens = purpose == "interview.report" ? 4_000 : isProfile ? 3_000 : 2_000;
        return new(purpose,
            isProfile ? ProfilePromptVersion : PromptVersion,
            CurrentModelVersion,
            RubricVersion,
            isProfile ? ProfileSchemaVersion : SchemaVersion,
            Bound(input), schema, maxOutputTokens, correlationId.ToString("N"));
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

    [LoggerMessage(LogLevel.Information,
        "Resume {ResumeId} extracted with {PageCount} pages, {CharacterCount} chars, {WordCount} words, method {ExtractionMethod}, quality {QualityScore}, OCR fallback {OcrFallbackUsed}, warnings {Warnings}")]
    private static partial void ResumeExtractionMeasured(
        ILogger logger, Guid resumeId, int pageCount, int characterCount, int wordCount,
        string extractionMethod, double qualityScore, string warnings, bool ocrFallbackUsed);

    [LoggerMessage(LogLevel.Warning, "Resume {ResumeId} local extraction failed with {ExceptionType}; trying document fallback")]
    private static partial void LocalExtractionFailed(ILogger logger, Guid resumeId, string exceptionType);

    [LoggerMessage(LogLevel.Error, "Resume {ResumeId} storage integrity check failed: actualBytes={ActualBytes} expectedBytes={ExpectedBytes}")]
    private static partial void StorageIntegrityFailed(ILogger logger, Guid resumeId, long actualBytes, long expectedBytes);

    [LoggerMessage(LogLevel.Information, "Resume {ResumeId} entered document OCR fallback after {LocalQuality} local quality")]
    private static partial void OcrFallbackStarted(ILogger logger, Guid resumeId, string localQuality);

    private static ResumeView MapResume(ResumeRecord resume) => new(
        resume.Id,
        resume.StoredFile.FileName,
        resume.StoredFile.ContentType,
        resume.StoredFile.Size,
        resume.Status,
        resume.CreatedAt,
        resume.Status == PracticeValues.Failed ? "RESUME_EXTRACTION_FAILED" : null,
        resume.Status == PracticeValues.Failed ? ResumeExtractionFailureMessage : null);
    private static JobDescriptionView MapJobDescription(JobDescription jd) => new(jd.Id, jd.Title, jd.Content, jd.CreatedAt);
    private static ResumeAnalysisView MapAnalysis(ResumeAnalysis analysis) => new(
        analysis.Id,
        analysis.Status,
        ParseJson(analysis.Result),
        analysis.CreatedAt,
        analysis.CompletedAt,
        analysis.ErrorCode,
        analysis.Mode,
        ParseAnalysisContext(analysis.ContextJson, analysis.Mode),
        analysis.ResumeVersion,
        analysis.JobDescriptionVersion,
        analysis.ModelVersion,
        analysis.PromptVersion,
        analysis.SchemaVersion,
        analysis.RubricVersion,
        analysis.ProfileModelVersion,
        analysis.ProfilePromptVersion,
        analysis.ProfileSchemaVersion);
    private static InterviewView MapInterview(
        InterviewSession session,
        IEnumerable<InterviewQuestion> questions,
        IEnumerable<InterviewAnswer> answers,
        InterviewContinuationView? continuation = null) =>
        new(session.Id, session.Status, session.Role, session.Seniority, session.InterviewType, session.Difficulty, session.Version,
            questions.OrderBy(item => item.Sequence).Select(MapQuestion).ToArray(), answers.OrderBy(item => item.CreatedAt).Select(MapAnswer).ToArray(), session.CreatedAt, session.UpdatedAt, continuation);

    private async Task<InterviewContinuationView?> BuildContinuationAsync(
        Guid userId,
        InterviewSession session,
        CancellationToken cancellationToken)
    {
        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);
        return BuildContinuation(session, questionLimit);
    }

    private static InterviewContinuationView? BuildContinuation(InterviewSession session, int questionLimit)
    {
        if (session.Status != PracticeValues.Active)
            return null;

        var questions = session.Questions.Count;
        var answered = session.Answers.Count(answer => !string.IsNullOrWhiteSpace(answer.Content));
        var canFinishNow = answered >= MinimumReportAnswers;
        var allIssuedQuestionsAnswered = questions > 0 && answered >= questions;
        if (questions >= questionLimit && allIssuedQuestionsAnswered)
        {
            var isFreeCap = questionLimit <= InterviewQuestionValues.FreeQuestionLimit;
            return new InterviewContinuationView(
                isFreeCap ? InterviewContinuationValues.UpgradeRequired : InterviewContinuationValues.MaxQuestionsReached,
                canFinishNow,
                isFreeCap);
        }

        return new InterviewContinuationView(InterviewContinuationValues.InProgress, canFinishNow, false);
    }

    private async Task<int> GetQuestionLimitAsync(Guid userId, CancellationToken cancellationToken)
    {
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.InterviewQuestionLimit, cancellationToken);
        if (!access.Enabled)
            return InterviewQuestionValues.FreeQuestionLimit;
        if (access.Unlimited)
            return int.MaxValue;
        if (access.Limit is null or <= 0)
            return InterviewQuestionValues.FreeQuestionLimit;
        return Math.Max(InterviewQuestionValues.FreeQuestionLimit, access.Limit.Value);
    }
    private static QuestionView MapQuestion(InterviewQuestion question) => new(
        question.Id,
        question.Sequence,
        question.Kind,
        question.Topic,
        question.ParentQuestionId,
        question.Content,
        question.CreatedAt);
    private static AnswerView MapAnswer(InterviewAnswer answer) => new(answer.Id, answer.QuestionId, answer.Content, answer.DurationSeconds, ParseJson(answer.Evaluation), answer.CreatedAt);
    private static ReportView MapReport(InterviewReport report, IEnumerable<InterviewQuestion> questions, IEnumerable<InterviewAnswer> answers)
    {
        var orderedQuestions = questions.OrderBy(item => item.Sequence).ToArray();
        var questionReviews = BuildQuestionReviews(orderedQuestions, answers);
        var suggestions = questionReviews
            .Where(item => !string.IsNullOrWhiteSpace(item.SuggestedImprovedAnswer))
            .Select(item => new SuggestedImprovedAnswerView(item.QuestionId, item.Sequence, item.SuggestedImprovedAnswer!))
            .ToArray();
        var sample = new ReportSampleView(
            questionReviews.Length,
            orderedQuestions.Length,
            IsPartialReport(questionReviews.Length, orderedQuestions.Length));
        return new ReportView(
            report.Id,
            report.InterviewSessionId,
            report.OverallScore,
            ParseJson(report.Rubric) ?? default(JsonElement),
            ParseJson(report.Strengths) ?? default(JsonElement),
            ParseJson(report.Gaps) ?? default(JsonElement),
            ParseJson(report.ActionPlan) ?? default(JsonElement),
            report.Disclaimer,
            report.CreatedAt,
            BuildStarSummary(orderedQuestions, answers),
            questionReviews,
            suggestions,
            sample);
    }

    private static InterviewQuestionReviewView[] BuildQuestionReviews(
        IReadOnlyCollection<InterviewQuestion> questions,
        IEnumerable<InterviewAnswer> answers)
    {
        var answersByQuestion = answers
            .Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .GroupBy(item => item.QuestionId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.CreatedAt).First());
        return questions
            .Where(item => answersByQuestion.ContainsKey(item.Id))
            .Select(question =>
            {
                var answer = answersByQuestion[question.Id];
                var evaluation = TryDeserializeAnswerEvaluation(answer.Evaluation);
                return new InterviewQuestionReviewView(
                    question.Id,
                    question.Sequence,
                    question.Kind,
                    question.Topic,
                    question.ParentQuestionId,
                    question.Content,
                    answer.Content,
                    evaluation?.Scores ?? [],
                    evaluation?.Feedback ?? string.Empty,
                    evaluation?.Star,
                    evaluation?.Strengths ?? [],
                    evaluation?.Improvements ?? [],
                    evaluation?.ImprovedAnswer);
            })
            .ToArray();
    }

    private static bool IsPartialReport(int answeredQuestions, int issuedQuestions) =>
        answeredQuestions < issuedQuestions || answeredQuestions <= InterviewQuestionValues.FreeQuestionLimit;

    private static string BuildReportDisclaimer(int answeredQuestions, int issuedQuestions) =>
        IsPartialReport(answeredQuestions, issuedQuestions)
            ? $"{Disclaimer} This is a partial sample based on {answeredQuestions} of {issuedQuestions} answered questions; it is not a full competency assessment."
            : $"{Disclaimer} This coaching report is based on {answeredQuestions} answered questions.";
    private static JsonElement? ParseJson(string? value) => value is null ? null : JsonSerializer.Deserialize<JsonElement>(value);
    private static AnswerEvaluation? TryDeserializeAnswerEvaluation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<AnswerEvaluation>(value, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static ResumeAnalysisContextView? ParseAnalysisContext(string? value, string mode)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ResumeAnalysisContextView>(value, JsonOptions);
                if (parsed is not null) return parsed;
            }
            catch (JsonException)
            {
                // Legacy rows have no context snapshot. Keep their mode visible
                // without exposing raw JSON or failing read-only retrieval.
            }
        }

        return string.IsNullOrWhiteSpace(mode)
            ? null
            : new ResumeAnalysisContextView(mode, null, null, null);
    }

    private static AiOperationDefinition<ResumeAnalysisOutput> GetResumeAnalysisOperation(ResumeAnalysisMode mode) => mode switch
    {
        ResumeAnalysisMode.JobTargeted => AiOperations.ResumeAnalysis,
        ResumeAnalysisMode.FieldBenchmark => AiOperations.ResumeAnalysisFieldBenchmark,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static ResumeAnalysisContextView ReadAnalysisContext(ResumeAnalysis analysis)
    {
        ResumeAnalysisContextView? context;
        if (string.IsNullOrWhiteSpace(analysis.ContextJson))
        {
            // Rows created before CV Analysis v2 have no context snapshot. They
            // are backfilled as job-targeted by the migration and can safely use
            // their persisted job description below.
            context = string.IsNullOrWhiteSpace(analysis.Mode)
                ? null
                : new ResumeAnalysisContextView(analysis.Mode, null, null, null);
        }
        else
        {
            try
            {
                context = JsonSerializer.Deserialize<ResumeAnalysisContextView>(analysis.ContextJson, JsonOptions);
            }
            catch (JsonException)
            {
                context = null;
            }
        }

        if (context is null)
            throw InvalidAiOutput();
        if (!ResumeAnalysisModes.TryParse(context.Mode, out var mode))
            throw Validation("Mode phân tích CV không hợp lệ.", "RESUME_ANALYSIS_MODE_INVALID");
        if (!ResumeAnalysisModes.TryParse(analysis.Mode, out var persistedMode))
            throw Validation("Invalid persisted analysis mode.", "RESUME_ANALYSIS_MODE_INVALID");
        if (persistedMode != mode)
            throw Validation("Persisted analysis context does not match its mode.", "RESUME_ANALYSIS_CONTEXT_INVALID");
        if (mode == ResumeAnalysisMode.JobTargeted)
        {
            if (analysis.JobDescriptionId is null || analysis.JobDescriptionVersion is null || analysis.JobDescription is null ||
                context.Industry is not null || context.TargetRole is not null || context.Seniority is not null)
                throw Validation("Job-targeted analysis context is inconsistent.", "RESUME_ANALYSIS_CONTEXT_INVALID");
        }
        else if (analysis.JobDescriptionId is not null || analysis.JobDescriptionVersion is not null ||
                 string.IsNullOrWhiteSpace(context.Industry) ||
                 string.IsNullOrWhiteSpace(context.TargetRole) ||
                 string.IsNullOrWhiteSpace(context.Seniority))
        {
            throw Validation("Field-benchmark analysis context is inconsistent.", "RESUME_ANALYSIS_CONTEXT_INVALID");
        }
        return context with { Mode = persistedMode.ToWireValue() };
    }

    private static NormalizedResumeAnalysisCommand NormalizeAnalysisCommand(StartResumeAnalysisCommand command)
    {
        if (command.ResumeId == Guid.Empty)
            throw Validation("Resume không hợp lệ.", "RESUME_ANALYSIS_CONTEXT_INVALID");
        if (!ResumeAnalysisModes.TryParse(command.Mode, out var mode))
            throw Validation("Mode phân tích CV không hợp lệ.", "RESUME_ANALYSIS_MODE_INVALID");

        var industry = NormalizeOptional(command.Industry, 160);
        var targetRole = NormalizeOptional(command.TargetRole, 160);
        var seniority = NormalizeOptional(command.Seniority, 80);
        if (command.Industry is not null && industry is null ||
            command.TargetRole is not null && targetRole is null ||
            command.Seniority is not null && seniority is null)
            throw Validation("Ngữ cảnh phân tích CV quá dài.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        if (mode == ResumeAnalysisMode.JobTargeted)
        {
            if (command.JobDescriptionId is null || industry is not null || targetRole is not null || seniority is not null)
                throw Validation("Job-targeted analysis yêu cầu JobDescription và không nhận ngữ cảnh benchmark.", "RESUME_ANALYSIS_CONTEXT_INVALID");
            return new(mode, mode.ToWireValue(), command.JobDescriptionId, null, null, null);
        }

        if (command.JobDescriptionId is not null || industry is null || targetRole is null || seniority is null)
            throw Validation("Field-benchmark analysis yêu cầu industry, targetRole và seniority; không nhận JobDescription.", "RESUME_ANALYSIS_CONTEXT_INVALID");
        return new(mode, mode.ToWireValue(), null, industry, targetRole, seniority);
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (value is null) return null;
        var normalized = value.Trim();
        return normalized.Length == 0 || normalized.Length > maxLength ? null : normalized;
    }

    private sealed record NormalizedResumeAnalysisCommand(
        ResumeAnalysisMode Mode,
        string ModeWire,
        Guid? JobDescriptionId,
        string? Industry,
        string? TargetRole,
        string? Seniority);

    private static BusinessException Validation(string message, string code = "VALIDATION_ERROR") => new(code, message, BusinessErrorKind.Validation);
    private static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    private static BusinessException Conflict(string code, string message) => new(code, message, BusinessErrorKind.Conflict);
    private static BusinessException InvalidState() => Conflict("INVALID_INTERVIEW_STATE", "Trạng thái interview không hợp lệ cho thao tác này.");
    private static BusinessException InterviewUpgradeRequired() =>
        new("INTERVIEW_UPGRADE_REQUIRED", "Hãy nâng cấp gói để tiếp tục phiên phỏng vấn này.", BusinessErrorKind.Forbidden);
    private static BusinessException InterviewLimitReached() =>
        Conflict("INTERVIEW_MAX_QUESTIONS_REACHED", "Phiên phỏng vấn đã đạt giới hạn câu hỏi.");
    private static BusinessException InvalidAiOutput() => new("AI_OUTPUT_INVALID", "AI trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);
    private static BusinessException AiUnavailable(AiProviderException exception) => new(
        exception.Kind == AiProviderFailureKind.RateLimited ? "AI_RATE_LIMITED" : "AI_PROVIDER_UNAVAILABLE",
        "Dịch vụ AI tạm thời chưa sẵn sàng. Vui lòng thử lại sau.",
        BusinessErrorKind.ExternalFailure);
}
