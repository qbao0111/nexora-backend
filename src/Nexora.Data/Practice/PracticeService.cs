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
using Nexora.Data.Career;
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
    private const int UnlimitedQuestionPlanBatchSize = 20;
    private const int MaximumHistoryPageSize = 100;
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
        await LockUserAsync(userId, cancellationToken);
        var duplicate = await dbContext.StoredFiles.AsNoTracking().SingleOrDefaultAsync(file => file.StorageKey == upload.StorageKey, cancellationToken);
        if (duplicate is not null)
        {
            var existing = await dbContext.Resumes.AsNoTracking().Include(resume => resume.StoredFile)
                .SingleAsync(resume => resume.StoredFileId == duplicate.Id && resume.UserId == userId, cancellationToken);
            if (existing.DeletedAt is not null)
                throw Conflict("RESUME_DELETED", "CV đã bị xóa và không thể khôi phục bằng finalize lại.");
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
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }
            dbContext.ChangeTracker.Clear();
            await using var recoveryTransaction = await BeginTransactionAsync(cancellationToken);
            await LockUserAsync(userId, cancellationToken);
            var concurrent = await dbContext.StoredFiles.AsNoTracking()
                .SingleOrDefaultAsync(file => file.StorageKey == upload.StorageKey && file.UserId == userId, cancellationToken);
            if (concurrent is null) throw;
            var existing = await dbContext.Resumes.AsNoTracking().Include(item => item.StoredFile)
                .SingleOrDefaultAsync(item => item.StoredFileId == concurrent.Id && item.UserId == userId, cancellationToken);
            if (existing is null) throw;
            if (existing.DeletedAt is not null)
                throw Conflict("RESUME_DELETED", "CV đã bị xóa và không thể khôi phục bằng finalize lại.");
            await CommitAsync(recoveryTransaction, cancellationToken);
            return MapResume(existing);
        }
    }

    public async Task DeleteResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);

        var resume = await dbContext.Resumes.SingleOrDefaultAsync(
            item => item.Id == resumeId && item.UserId == userId,
            cancellationToken) ?? throw NotFound();
        if (resume.DeletedAt is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        resume.DeletedAt = now;
        resume.UpdatedAt = now;
        resume.StorageDeleteNextAttemptAt = now;

        var profile = await dbContext.UserProfiles.SingleOrDefaultAsync(
            item => item.UserId == userId && item.PrimaryResumeId == resumeId,
            cancellationToken);
        if (profile is not null)
        {
            profile.PrimaryResumeId = null;
            profile.UpdatedAt = now;
        }

        EnqueueResourceChanged(userId, "resume", resumeId, "deleted", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    public async Task<ResumeView> GetResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken)
    {
        var resume = await dbContext.Resumes.AsNoTracking().Include(item => item.StoredFile)
            .SingleOrDefaultAsync(item => item.Id == resumeId && item.UserId == userId && item.DeletedAt == null, cancellationToken)
            ?? throw NotFound();
        return MapResume(resume);
    }

    public async Task<IReadOnlyList<ResumeView>> GetResumesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var query = dbContext.Resumes.AsNoTracking().Where(item => item.UserId == userId && item.DeletedAt == null);
        if (string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            return (await query.Select(item => new ResumeView(
                    item.Id,
                    item.StoredFile.FileName,
                    item.StoredFile.ContentType,
                    item.StoredFile.Size,
                    item.Status,
                    item.CreatedAt,
                    item.Status == PracticeValues.Failed ? "RESUME_EXTRACTION_FAILED" : null,
                    item.Status == PracticeValues.Failed ? ResumeExtractionFailureMessage : null))
                .ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .ToArray();
        }

        return await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new ResumeView(
                item.Id,
                item.StoredFile.FileName,
                item.StoredFile.ContentType,
                item.StoredFile.Size,
                item.Status,
                item.CreatedAt,
                item.Status == PracticeValues.Failed ? "RESUME_EXTRACTION_FAILED" : null,
                item.Status == PracticeValues.Failed ? ResumeExtractionFailureMessage : null))
            .ToArrayAsync(cancellationToken);
    }

    private async Task WaitForResumeReadyAsync(Guid resumeId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var state = await dbContext.Resumes.AsNoTracking().Where(item => item.Id == resumeId)
                .Select(item => new { item.Status, item.DeletedAt })
                .SingleAsync(cancellationToken);
            if (state.DeletedAt is not null) throw NotFound();
            if (state.Status == PracticeValues.Ready) return;
            if (state.Status == PracticeValues.Failed)
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

    public async Task<IReadOnlyList<JobDescriptionView>> GetJobDescriptionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var query = dbContext.JobDescriptions.AsNoTracking().Where(item => item.UserId == userId);
        if (string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            return (await query
                    .Select(item => new JobDescriptionView(item.Id, item.Title, item.Content, item.CreatedAt))
                    .ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .ToArray();
        }

        return await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new JobDescriptionView(item.Id, item.Title, item.Content, item.CreatedAt))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<JobDescriptionView> GetJobDescriptionAsync(Guid userId, Guid jobDescriptionId, CancellationToken cancellationToken)
    {
        var jobDescription = await dbContext.JobDescriptions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobDescriptionId && item.UserId == userId, cancellationToken)
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
        var key = RequireKey(idempotencyKey);
        var normalized = await ResolveResumeAnalysisCommandAsync(userId, command, cancellationToken);
        var fingerprint = Fingerprint(
            normalized.ResumeId,
            normalized.ModeWire,
            normalized.JobDescriptionId,
            normalized.Industry,
            normalized.TargetRole,
            normalized.Seniority);
        var prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);
        var resume = await dbContext.Resumes.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == normalized.ResumeId && item.UserId == userId && item.DeletedAt == null,
            cancellationToken)
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
        await LockUserAsync(userId, cancellationToken);
        resume = await dbContext.Resumes.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == normalized.ResumeId && item.UserId == userId && item.DeletedAt == null,
            cancellationToken)
            ?? throw NotFound();
        if (resume.Status != PracticeValues.Ready) throw Conflict("RESUME_NOT_READY", "CV chưa sẵn sàng để phân tích.");
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

    public async Task<ResumeAnalysisHistoryPage> GetResumeAnalysisHistoryAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var paging = NormalizePaging(page, pageSize);
        var query = dbContext.ResumeAnalyses.AsNoTracking().Where(item => item.UserId == userId);
        var totalCount = await query.CountAsync(cancellationToken);
        var skip = PagingSkip(paging.Page, paging.PageSize);
        var projectedQuery = query.Select(item => new
        {
            item.Id,
            item.ResumeId,
            item.Mode,
            item.Status,
            item.CreatedAt,
            item.CompletedAt,
            item.ContextJson,
            item.ErrorCode
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

        var items = rows.Select(item => new ResumeAnalysisHistoryItem(
            item.Id,
            item.ResumeId,
            item.Mode,
            item.Status,
            item.CreatedAt,
            item.CompletedAt,
            ParseAnalysisContext(item.ContextJson, item.Mode),
            item.ErrorCode)).ToArray();
        return new(items, paging.Page, paging.PageSize, totalCount, skip + items.Length < totalCount);
    }

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

    public async Task<AnswerResult> SubmitAnswerAsync(
        Guid userId, Guid interviewId, Guid questionId, string content, int? durationSeconds, string idempotencyKey, CancellationToken cancellationToken)
    {
        var normalizedContent = content?.Trim() ?? string.Empty;
        if (normalizedContent.Length is 0 or > 12_000 || durationSeconds is < 0 or > 7200)
            throw Validation("Câu trả lời không hợp lệ.");
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId, questionId, normalizedContent, durationSeconds);
        var prior = await FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
        if (prior is not null) return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
        if (prior is not null) return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);
        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        // A concurrent replay can wait on the session lock after its initial
        // idempotency read. Re-check after the lock so it replays the committed
        // answer instead of colliding with the official-answer unique key.
        prior = await FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status != PracticeValues.Active) throw InvalidState();
        var question = session.Questions.SingleOrDefault(item => item.Id == questionId) ?? throw NotFound();
        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);
        if (question.ReleasedAt is null || question.Sequence > questionLimit)
            throw InvalidState();
        if (session.Answers.Any(item => item.QuestionId == questionId))
            throw Conflict("ANSWER_ALREADY_EXISTS", "Câu hỏi đã có câu trả lời chính thức.");
        if (session.Questions.Any(item => item.Sequence < question.Sequence &&
                item.ReleasedAt is not null && session.Answers.All(answer => answer.QuestionId != item.Id)))
            throw InvalidState();

        var now = timeProvider.GetUtcNow();
        var answer = new InterviewAnswer
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InterviewSessionId = session.Id,
            QuestionId = question.Id,
            Content = normalizedContent,
            DurationSeconds = durationSeconds,
            Evaluation = null,
            EvaluationStatus = InterviewAnswerEvaluationStates.Queued,
            CreatedAt = now
        };
        dbContext.Add(answer);
        var nextQuestion = session.Questions
            .Where(item => item.Sequence > question.Sequence && item.Sequence <= questionLimit && item.ReleasedAt is null)
            .OrderBy(item => item.Sequence)
            .FirstOrDefault();
        if (nextQuestion is not null)
        {
            nextQuestion.ReleasedAt = now;
        }
        else if (session.Questions.Max(item => item.Sequence) < questionLimit &&
                 !await HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
        {
            dbContext.Add(Outbox("InterviewQuestionPlanRequested", "interview", session.Id, now));
        }
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(
            Outbox("InterviewAnswerEvaluationRequested", "interviewAnswer", answer.Id, now),
            Idempotency(userId, "interview.answer", key, fingerprint, answer.Id, now));
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
        return new AnswerResult(MapAnswer(answer, session.Status), nextQuestion is null ? null : MapQuestion(nextQuestion), isComplete, continuation);
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
        // A concurrent replay can wait on the interview row lock after its
        // initial idempotency reads. Re-check after acquiring the lock so it
        // replays the committed continuation instead of creating another
        // idempotency record or question-plan job.
        prior = await FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status != PracticeValues.Active) throw InvalidState();
        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);

        var released = session.Questions.Where(item => item.ReleasedAt is not null).OrderBy(item => item.Sequence).ToArray();
        if (released.Length < InterviewQuestionValues.FreeQuestionLimit ||
            released.Count(item => session.Answers.Any(answer => answer.QuestionId == item.Id)) < InterviewQuestionValues.FreeQuestionLimit)
            throw InvalidState();

        var existingPending = session.Questions
            .OrderBy(item => item.Sequence)
            .FirstOrDefault(item => item.ReleasedAt is not null && session.Answers.All(answer => answer.QuestionId != item.Id));
        if (existingPending is not null)
        {
            var replayAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.continue", key, fingerprint, session.Id, replayAt));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, interviewId, cancellationToken);
        }

        if (session.Questions.Count >= questionLimit)
        {
            if (questionLimit <= InterviewQuestionValues.FreeQuestionLimit)
                throw InterviewUpgradeRequired();
            throw InterviewLimitReached();
        }
        if (await HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
        {
            var pendingAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.continue", key, fingerprint, session.Id, pendingAt));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, interviewId, cancellationToken);
        }
        var now = timeProvider.GetUtcNow();
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(
            Idempotency(userId, "interview.continue", key, fingerprint, session.Id, now),
            Outbox("InterviewQuestionPlanRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await GetInterviewAsync(userId, interviewId, cancellationToken);
    }

    public async Task<InterviewView> RetryQuestionPreparationAsync(
        Guid userId,
        Guid interviewId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.questions.retry", key, fingerprint, cancellationToken);
        if (prior is not null)
            return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindIdempotentAsync(userId, "interview.questions.retry", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        }

        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        // A concurrent replay can wait on the interview row lock after its
        // initial idempotency reads. Re-check after acquiring the lock so it
        // replays the committed retry instead of reporting a transient state
        // conflict.
        prior = await FindIdempotentAsync(userId, "interview.questions.retry", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status != PracticeValues.Active)
            throw InvalidState();

        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);
        if (questionLimit <= InterviewQuestionValues.FreeQuestionLimit)
            throw InterviewUpgradeRequired();

        var released = session.Questions.Where(item => item.ReleasedAt is not null).ToArray();
        if (released.Any(item => session.Answers.All(answer => answer.QuestionId != item.Id)))
            throw new BusinessException(
                "INTERVIEW_QUESTION_ALREADY_READY",
                "Câu hỏi tiếp theo đã sẵn sàng.",
                BusinessErrorKind.Conflict);
        if (session.Questions.Count >= questionLimit)
            throw InterviewLimitReached();
        if (await HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
            throw new BusinessException(
                "INTERVIEW_QUESTION_PREPARATION_IN_PROGRESS",
                "Câu hỏi tiếp theo đang được chuẩn bị.",
                BusinessErrorKind.Conflict);

        var latestPlan = await GetLatestQuestionPlanJobAsync(session.Id, cancellationToken);
        if (latestPlan?.Status != BillingValues.Failed)
            throw new BusinessException(
                "INTERVIEW_QUESTION_PREPARATION_NOT_FAILED",
                "Chưa có lỗi chuẩn bị câu hỏi cần thử lại.",
                BusinessErrorKind.Conflict);

        var now = timeProvider.GetUtcNow();
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(
            Idempotency(userId, "interview.questions.retry", key, fingerprint, session.Id, now),
            Outbox("InterviewQuestionPlanRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
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
            return await GetInterviewAsync(userId, session.Id, cancellationToken);
        }
        if (session.Status == PracticeValues.Completing)
        {
            var retryAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, retryAt));
            await TryQueueReportIfReadyAsync(session.Id, retryAt, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, session.Id, cancellationToken);
        }
        var answeredCount = session.Answers.Count(answer => !string.IsNullOrWhiteSpace(answer.Content));
        if (session.Status != PracticeValues.Active || session.Questions.Count == 0 ||
            answeredCount < MinimumReportAnswers || answeredCount != session.Answers.Count)
            throw InvalidState();
        var now = timeProvider.GetUtcNow();
        session.Status = PracticeValues.Completing;
        session.Version++;
        session.UpdatedAt = now;
        dbContext.Add(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, now));
        await TryQueueReportIfReadyAsync(session.Id, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await GetInterviewAsync(userId, session.Id, cancellationToken);
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
        if (session.Status == PracticeValues.Completing)
            await TryQueueReportIfReadyAsync(session.Id, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await GetInterviewAsync(userId, session.Id, cancellationToken);
    }

    public async Task<InterviewView> RetryResultsAsync(
        Guid userId,
        Guid interviewId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.results.retry", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        var lockedPrior = await FindIdempotentAsync(userId, "interview.results.retry", key, fingerprint, cancellationToken);
        if (lockedPrior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, lockedPrior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        if (session.Status is not PracticeValues.Completing and not PracticeValues.Completed)
            throw InvalidState();

        var now = timeProvider.GetUtcNow();
        var failedAnswers = session.Answers.Where(item => item.EvaluationStatus == InterviewAnswerEvaluationStates.Failed).ToArray();
        foreach (var answer in failedAnswers)
        {
            answer.EvaluationStatus = InterviewAnswerEvaluationStates.Queued;
            answer.EvaluationErrorCode = null;
            answer.EvaluationCompletedAt = null;
            if (!await dbContext.OutboxEvents.AnyAsync(item => item.AggregateId == answer.Id &&
                    item.Type == "InterviewAnswerEvaluationRequested" &&
                    (item.Status == BillingValues.Pending || item.Status == BillingValues.Processing), cancellationToken))
                dbContext.Add(Outbox("InterviewAnswerEvaluationRequested", "interviewAnswer", answer.Id, now));
        }

        if (session.Status == PracticeValues.Completing)
        {
            var reportJobs = dbContext.OutboxEvents.AsNoTracking()
                .Where(item => item.AggregateId == session.Id && item.Type == "InterviewReportRequested");
            var latestReportJob = dbContext.Database.IsNpgsql()
                ? await reportJobs.OrderByDescending(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken)
                : (await reportJobs.ToArrayAsync(cancellationToken)).OrderByDescending(item => item.CreatedAt).FirstOrDefault();
            if (failedAnswers.Length == 0 && latestReportJob?.Status == BillingValues.Failed &&
                !await HasPendingReportJobAsync(session.Id, cancellationToken))
                dbContext.Add(Outbox("InterviewReportRequested", "interview", session.Id, now));
            await TryQueueReportIfReadyAsync(session.Id, now, cancellationToken);
        }
        dbContext.Add(Idempotency(userId, "interview.results.retry", key, fingerprint, session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await GetInterviewAsync(userId, session.Id, cancellationToken);
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
            item.Type == "InterviewStartRequested" || item.Type == "InterviewReportRequested" ||
            item.Type == "InterviewAnswerEvaluationRequested" || item.Type == "InterviewQuestionPlanRequested");
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
                    case "InterviewAnswerEvaluationRequested": await EvaluateInterviewAnswerAsync(job, cancellationToken); break;
                    case "InterviewQuestionPlanRequested": await PrepareInterviewQuestionsAsync(job, cancellationToken); break;
                }
                JobCompleted(logger, job.Id, job.Type, job.AggregateId, queueLagSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                await FailJobAsync(job, exception, cancellationToken);
                JobFailed(logger, job.Id, job.Type, job.AggregateId, exception.GetType().Name,
                    queueLagSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        return claimedCount + await ProcessPendingResumeStorageCleanupAsync(cancellationToken);
    }

    private async Task<int> ProcessPendingResumeStorageCleanupAsync(CancellationToken cancellationToken)
    {
        const int batchSize = 20;
        var now = timeProvider.GetUtcNow();
        var pendingCleanup = dbContext.Resumes.AsNoTracking().Where(resume =>
            resume.DeletedAt != null && resume.StorageDeletedAt == null &&
            !dbContext.OutboxEvents.Any(job => job.AggregateId == resume.Id &&
                job.Type == "ResumeExtractionRequested" &&
                (job.Status == BillingValues.Pending || job.Status == BillingValues.Processing)));

        ResumeStorageCleanupCandidate[] candidates;
        if (dbContext.Database.IsNpgsql())
        {
            candidates = await pendingCleanup
                .Where(resume => resume.StorageDeleteNextAttemptAt == null || resume.StorageDeleteNextAttemptAt <= now)
                .OrderBy(resume => resume.StorageDeleteNextAttemptAt)
                .ThenBy(resume => resume.DeletedAt)
                .Take(batchSize)
                .Select(resume => new ResumeStorageCleanupCandidate(
                    resume.Id, resume.StorageDeleteAttempts, resume.StorageDeleteNextAttemptAt))
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            var rows = await pendingCleanup
                .Select(resume => new ResumeStorageCleanupCandidate(
                    resume.Id, resume.StorageDeleteAttempts, resume.StorageDeleteNextAttemptAt))
                .ToArrayAsync(cancellationToken);
            candidates = rows
                .Where(resume => resume.NextAttemptAt is null || resume.NextAttemptAt <= now)
                .Take(batchSize)
                .ToArray();
        }

        var attempted = 0;
        foreach (var candidate in candidates)
        {
            var leaseUntil = now.AddMinutes(5);
            var claim = dbContext.Resumes.Where(resume =>
                resume.Id == candidate.ResumeId && resume.DeletedAt != null && resume.StorageDeletedAt == null &&
                resume.StorageDeleteNextAttemptAt == candidate.NextAttemptAt &&
                !dbContext.OutboxEvents.Any(job => job.AggregateId == resume.Id &&
                    job.Type == "ResumeExtractionRequested" &&
                    (job.Status == BillingValues.Pending || job.Status == BillingValues.Processing)));
            var claimed = await claim.ExecuteUpdateAsync(setters => setters
                .SetProperty(resume => resume.StorageDeleteAttempts, resume => resume.StorageDeleteAttempts + 1)
                .SetProperty(resume => resume.StorageDeleteNextAttemptAt, leaseUntil), cancellationToken);
            if (claimed == 0) continue;

            attempted++;
            var attemptNumber = candidate.Attempts + 1;
            var storageKey = await dbContext.Resumes.AsNoTracking()
                .Where(resume => resume.Id == candidate.ResumeId)
                .Select(resume => resume.StoredFile.StorageKey)
                .SingleAsync(cancellationToken);
            try
            {
                await storageProvider.DeleteAsync(storageKey, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                var retryAt = timeProvider.GetUtcNow().Add(ResumeStorageCleanupDelay(attemptNumber));
                await dbContext.Resumes
                    .Where(resume => resume.Id == candidate.ResumeId && resume.StorageDeletedAt == null &&
                                     resume.StorageDeleteNextAttemptAt == leaseUntil)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(resume => resume.StorageDeleteNextAttemptAt, retryAt), cancellationToken);
                ResumeStorageCleanupFailed(logger, candidate.ResumeId, attemptNumber);
                continue;
            }

            var deletedAt = timeProvider.GetUtcNow();
            await dbContext.Resumes
                .Where(resume => resume.Id == candidate.ResumeId && resume.DeletedAt != null &&
                                 resume.StorageDeletedAt == null && resume.StorageDeleteNextAttemptAt == leaseUntil)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(resume => resume.StorageDeletedAt, deletedAt)
                    .SetProperty(resume => resume.StorageDeleteNextAttemptAt, (DateTimeOffset?)null), cancellationToken);
        }

        return attempted;
    }

    private static TimeSpan ResumeStorageCleanupDelay(int attemptNumber)
    {
        var exponent = Math.Clamp(attemptNumber - 1, 0, 10);
        return TimeSpan.FromSeconds(Math.Min(21_600, 30 * (1 << exponent)));
    }

    private sealed record ResumeStorageCleanupCandidate(
        Guid ResumeId,
        int Attempts,
        DateTimeOffset? NextAttemptAt);

    private async Task ExtractResumeAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var resume = await dbContext.Resumes.Include(item => item.StoredFile).SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (!await TrySetResumeExtractionStatusAsync(
                resume, job, PracticeValues.Extracting, notify: false, markProcessed: false, cancellationToken))
            return;

        await using var source = await storageProvider.OpenReadAsync(resume.StoredFile.StorageKey, cancellationToken);
        await using var buffered = await ReadStoredFileBoundedAsync(source, resume.StoredFile.Size, cancellationToken);
        var actualChecksum = buffered.Length == resume.StoredFile.Size
            ? Convert.ToHexString(SHA256.HashData(buffered.GetBuffer().AsSpan(0, checked((int)buffered.Length)))).ToLowerInvariant()
            : string.Empty;
        if (buffered.Length != resume.StoredFile.Size ||
            !string.Equals(actualChecksum, resume.StoredFile.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            await TrySetResumeExtractionStatusAsync(
                resume, job, PracticeValues.Failed, notify: true, markProcessed: true, cancellationToken);
            StorageIntegrityFailed(logger, resume.Id, buffered.Length, resume.StoredFile.Size);
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

        if (!await TrySetResumeExtractionStatusAsync(
                resume, job, PracticeValues.OcrFallback, notify: false, markProcessed: false, cancellationToken))
            return;
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

    private async Task<bool> TrySetResumeExtractionStatusAsync(
        ResumeRecord resume,
        OutboxEvent job,
        string status,
        bool notify,
        bool markProcessed,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(resume.UserId, cancellationToken);
        await dbContext.Entry(resume).ReloadAsync(cancellationToken);
        if (resume.DeletedAt is not null)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return false;
        }

        resume.Status = status;
        resume.UpdatedAt = timeProvider.GetUtcNow();
        if (notify) EnqueueResourceChanged(resume.UserId, "resume", resume.Id, status, resume.UpdatedAt);
        if (markProcessed) MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return true;
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
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(resume.UserId, cancellationToken);
        await dbContext.Entry(resume).ReloadAsync(cancellationToken);
        if (resume.DeletedAt is not null)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return;
        }

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
        await CommitAsync(transaction, cancellationToken);
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

    private static bool HasUsableResumeContext(ResumeRecord? resume) => resume is { DeletedAt: null };

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

        await using (var startTransaction = await BeginTransactionAsync(cancellationToken))
        {
            await LockUserAsync(analysis.UserId, cancellationToken);
            await dbContext.Entry(analysis.Resume).ReloadAsync(cancellationToken);
            if (analysis.Resume.DeletedAt is not null &&
                analysis.Status is PracticeValues.Queued or PracticeValues.Processing)
            {
                var now = timeProvider.GetUtcNow();
                analysis.Status = PracticeValues.Failed;
                analysis.ErrorCode = "RESUME_DELETED";
                analysis.UpdatedAt = now;
                EnqueueResourceChanged(analysis.UserId, "resumeAnalysis", analysis.Id, analysis.Status, now);
                MarkProcessed(job);
                if (analysis.UsageReservationId.HasValue)
                    await featureEntitlementService.VoidAsync(analysis.UserId, analysis.UsageReservationId.Value, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(startTransaction, cancellationToken);
                return;
            }

            if (analysis.Status == PracticeValues.Queued)
            {
                analysis.Status = PracticeValues.Processing;
                analysis.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else if (analysis.Status != PracticeValues.Processing)
            {
                MarkProcessed(job);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(startTransaction, cancellationToken);
                return;
            }
            await CommitAsync(startTransaction, cancellationToken);
        }
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

        await using (var activationGate = await BeginTransactionAsync(cancellationToken))
        {
            await LockUserAsync(snapshot.UserId, cancellationToken);
            await dbContext.Entry(snapshot).ReloadAsync(cancellationToken);
            if (snapshot.Resume is not null)
                await dbContext.Entry(snapshot.Resume).ReloadAsync(cancellationToken);
            if (snapshot.Status == PracticeValues.Starting && snapshot.Resume?.DeletedAt is not null)
            {
                var deletedReservation = await dbContext.UsageEvents.AsNoTracking()
                    .SingleAsync(item => item.Id == snapshot.ReservationEventId, cancellationToken);
                var deletedEntitlement = await FindEntitlementForUpdateAsync(deletedReservation.EntitlementId, cancellationToken)
                    ?? throw InvalidState();
                var deletionGateAt = timeProvider.GetUtcNow();
                FinalizeReservation(deletedEntitlement, deletedReservation, BillingValues.Void, deletionGateAt);
                snapshot.Status = PracticeValues.Failed;
                snapshot.Version++;
                snapshot.UpdatedAt = deletionGateAt;
                EnqueueResourceChanged(snapshot.UserId, "interview", snapshot.Id, snapshot.Status, deletionGateAt);
                MarkProcessed(job);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(activationGate, cancellationToken);
                return;
            }
            await CommitAsync(activationGate, cancellationToken);
        }

        if (snapshot.Status != PracticeValues.Starting)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }
        var hasUsableResumeContext = HasUsableResumeContext(snapshot.Resume);
        var profile = hasUsableResumeContext
            ? await EnsureResumeProfileAsync(snapshot.Resume!, snapshot.Id, cancellationToken)
            : null;
        var questionLimit = await GetQuestionLimitAsync(snapshot.UserId, cancellationToken);
        var preparedQuestions = await GeneratePreparedQuestionsAsync(
            snapshot,
            profile,
            startSequence: 1,
            endSequence: PreparationLimit(questionLimit),
            hasUsableResumeContext,
            cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (session.Status != PracticeValues.Starting) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); await CommitAsync(transaction, cancellationToken); return; }
        var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
        var entitlement = await FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
        var now = timeProvider.GetUtcNow();
        dbContext.InterviewQuestions.AddRange(preparedQuestions.Select((prepared, index) => new InterviewQuestion
        {
            Id = Guid.NewGuid(),
            InterviewSessionId = session.Id,
            Sequence = prepared.Sequence,
            Kind = InterviewQuestionValues.Primary,
            Topic = prepared.Topic,
            Content = prepared.Content,
            PromptVersion = prepared.PromptVersion,
            ModelVersion = prepared.ModelVersion,
            CreatedAt = now,
            ReleasedAt = index == 0 ? now : null
        }));
        FinalizeReservation(entitlement, reservation, BillingValues.Consume, now);
        session.Status = PracticeValues.Active;
        EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
        session.Version++;
        session.UpdatedAt = now;
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task<IReadOnlyCollection<PreparedInterviewQuestion>> GeneratePreparedQuestionsAsync(
        InterviewSession session,
        ResumeProfile? profile,
        int startSequence,
        int endSequence,
        bool hasUsableResumeContext,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedInterviewQuestion>();
        for (var sequence = startSequence; sequence <= endSequence; sequence++)
        {
            var topic = sequence == 1 && !string.IsNullOrWhiteSpace(session.FocusTopic)
                ? session.FocusTopic!
                : sequence <= InterviewQuestionValues.FreeQuestionLimit
                    ? InterviewQuestionValues.FreePrimaryTopicForSequence(
                        session.InterviewType,
                        sequence,
                        hasUsableResumeContext,
                        session.JobDescription is not null)
                    : InterviewQuestionValues.PaidTopicForContext(
                        session.InterviewType,
                        hasUsableResumeContext,
                        session.JobDescription is not null);
            var context = resumeContextBuilder.BuildInterviewQuestionContext(
                session.Role,
                session.Seniority,
                session.InterviewType,
                session.Difficulty,
                session.JobDescription?.Content,
                profile,
                sequence,
                topic);
            var result = await structuredAiExecutor.ExecuteAsync<GeneratedQuestion>(
                AiOperations.InterviewFirstQuestion,
                context,
                new AiOperationContext(
                    session.Id.ToString("N"),
                    session.UserId,
                    Metadata: new Dictionary<string, string>
                    {
                        ["questionSequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["questionKind"] = InterviewQuestionValues.Primary,
                        ["questionTopic"] = topic
                    }),
                cancellationToken);
            var content = result.Value.Content?.Trim() ?? string.Empty;
            if (content.Length == 0) throw InvalidAiOutput();
            prepared.Add(new PreparedInterviewQuestion(
                sequence,
                topic,
                content[..Math.Min(content.Length, 2_000)],
                result.PromptVersion,
                result.ModelVersion));
        }

        return prepared;
    }

    private async Task PrepareInterviewQuestionsAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.AsNoTracking()
            .Include(item => item.Resume)
            .Include(item => item.JobDescription)
            .SingleOrDefaultAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (snapshot is null || snapshot.Status != PracticeValues.Active)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var questionLimit = await GetQuestionLimitAsync(snapshot.UserId, cancellationToken);
        var existingMax = await dbContext.InterviewQuestions.AsNoTracking()
            .Where(item => item.InterviewSessionId == snapshot.Id)
            .Select(item => (int?)item.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
        var endSequence = PreparationLimit(questionLimit, existingMax + 1);
        if (existingMax >= endSequence)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var hasUsableResumeContext = HasUsableResumeContext(snapshot.Resume);
        var profile = hasUsableResumeContext
            ? TryReadResumeProfile(snapshot.Resume?.StructuredProfile)
            : null;
        var prepared = await GeneratePreparedQuestionsAsync(
            snapshot,
            profile,
            existingMax + 1,
            endSequence,
            hasUsableResumeContext,
            cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(snapshot.UserId, snapshot.Id, cancellationToken) ?? throw NotFound();
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        if (session.Status == PracticeValues.Active)
        {
            var known = session.Questions.Select(item => item.Sequence).ToHashSet();
            var now = timeProvider.GetUtcNow();
            foreach (var item in prepared.Where(item => known.Add(item.Sequence)))
            {
                dbContext.InterviewQuestions.Add(new InterviewQuestion
                {
                    Id = Guid.NewGuid(),
                    InterviewSessionId = session.Id,
                    Sequence = item.Sequence,
                    Kind = InterviewQuestionValues.Primary,
                    Topic = item.Topic,
                    Content = item.Content,
                    PromptVersion = item.PromptVersion,
                    ModelVersion = item.ModelVersion,
                    CreatedAt = now
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
            var next = session.Questions
                .Where(item => item.ReleasedAt is null && item.Sequence <= questionLimit)
                .OrderBy(item => item.Sequence)
                .FirstOrDefault(item => session.Questions
                    .Where(previous => previous.Sequence < item.Sequence && previous.ReleasedAt is not null)
                    .All(previous => session.Answers.Any(answer => answer.QuestionId == previous.Id)));
            if (next is not null)
            {
                next.ReleasedAt = now;
                session.Version++;
                session.UpdatedAt = now;
                EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
            }
        }

        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task EvaluateInterviewAnswerAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        await using (var transaction = await BeginTransactionAsync(cancellationToken))
        {
            var answer = await dbContext.InterviewAnswers
                .SingleOrDefaultAsync(item => item.Id == job.AggregateId, cancellationToken);
            if (answer is null || answer.EvaluationStatus is InterviewAnswerEvaluationStates.Ready or InterviewAnswerEvaluationStates.Failed)
            {
                MarkProcessed(job);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(transaction, cancellationToken);
                return;
            }

            answer.EvaluationStatus = InterviewAnswerEvaluationStates.Processing;
            answer.EvaluationErrorCode = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        var snapshot = await dbContext.InterviewAnswers
            .Include(item => item.Question)
            .Include(item => item.InterviewSession).ThenInclude(item => item.Resume)
            .Include(item => item.InterviewSession).ThenInclude(item => item.JobDescription)
            .Include(item => item.InterviewSession).ThenInclude(item => item.Answers)
            .SingleOrDefaultAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (snapshot is null) return;

        try
        {
            var question = snapshot.Question;
            var session = snapshot.InterviewSession;
            var isFollowUp = string.Equals(question.Kind, InterviewQuestionValues.Followup, StringComparison.Ordinal);
            var previousMissingElements = isFollowUp
                ? ReadMissingStarElements(session.Answers.FirstOrDefault(answer => answer.QuestionId == question.ParentQuestionId)?.Evaluation)
                : null;
            var hasUsableResumeContext = HasUsableResumeContext(session.Resume);
            var profile = hasUsableResumeContext
                ? TryReadResumeProfile(session.Resume?.StructuredProfile)
                : null;
            var answerContext = resumeContextBuilder.BuildAnswerEvaluationContext(
                session.Role,
                session.Seniority,
                session.InterviewType,
                session.JobDescription?.Content,
                question.Content,
                snapshot.Content,
                profile,
                question.Sequence,
                isFollowUp,
                previousMissingElements,
                question.Topic);
            var metadata = new Dictionary<string, string>
            {
                ["questionSequence"] = question.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isFollowup"] = isFollowUp ? "true" : "false",
                ["questionTopic"] = question.Topic
            };
            if (previousMissingElements is { Length: > 0 })
                metadata["followupTargetElements"] = string.Join(",", previousMissingElements);

            var result = await structuredAiExecutor.ExecuteAsync(
                AiOperations.InterviewEvaluate,
                answerContext,
                new AiOperationContext(
                    session.Id.ToString("N"),
                    snapshot.UserId,
                    ExpectedStar: question.Topic is InterviewQuestionValues.Behavioral or InterviewQuestionValues.BehavioralStar,
                    Metadata: metadata,
                    CandidateAnswer: snapshot.Content),
                cancellationToken);

            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var answer = await dbContext.InterviewAnswers.SingleAsync(item => item.Id == snapshot.Id, cancellationToken);
            if (answer.EvaluationStatus != InterviewAnswerEvaluationStates.Processing)
            {
                MarkProcessed(job);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(transaction, cancellationToken);
                return;
            }
            answer.Evaluation = JsonSerializer.Serialize(result.Value, JsonOptions);
            answer.EvaluationStatus = InterviewAnswerEvaluationStates.Ready;
            answer.EvaluationCompletedAt = timeProvider.GetUtcNow();
            answer.EvaluationErrorCode = null;
            EnqueueResourceChanged(snapshot.UserId, "interview", snapshot.InterviewSessionId, answer.EvaluationStatus, answer.EvaluationCompletedAt.Value);
            await dbContext.SaveChangesAsync(cancellationToken);
            await TryQueueReportIfReadyAsync(snapshot.InterviewSessionId, answer.EvaluationCompletedAt.Value, cancellationToken);
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await FailAnswerEvaluationAsync(job, snapshot, EvaluationErrorCode(exception), cancellationToken);
        }
    }

    private async Task FailAnswerEvaluationAsync(
        OutboxEvent job,
        InterviewAnswer snapshot,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var answer = await dbContext.InterviewAnswers.SingleOrDefaultAsync(item => item.Id == snapshot.Id, cancellationToken);
        if (answer is not null && answer.EvaluationStatus != InterviewAnswerEvaluationStates.Ready)
        {
            answer.EvaluationStatus = InterviewAnswerEvaluationStates.Failed;
            answer.Evaluation = null;
            answer.EvaluationErrorCode = errorCode;
            answer.EvaluationCompletedAt = timeProvider.GetUtcNow();
            EnqueueResourceChanged(answer.UserId, "interview", answer.InterviewSessionId, answer.EvaluationStatus, answer.EvaluationCompletedAt.Value);
        }
        job.Status = BillingValues.Failed;
        job.ProcessedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        EvaluationFailed(logger, AiOperations.InterviewEvaluate.Purpose, aiProvider.ModelVersion,
            snapshot.Id, snapshot.InterviewSessionId, errorCode, snapshot.Id.ToString("N"));
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
        var groundingTranscript = string.Join("\n", answeredQuestions.Select(item => item.Answer!.Content));
        var reportOperationContext = new AiOperationContext(
            snapshot.Id.ToString("N"),
            snapshot.UserId,
            GroundingTranscript: groundingTranscript);
        AiExecutionResult<InterviewReportOutput> execResult;
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
        }
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

    private async Task FailJobAsync(OutboxEvent job, Exception exception, CancellationToken cancellationToken)
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
            await LockUserAsync(resume.UserId, cancellationToken);
            await dbContext.Entry(resume).ReloadAsync(cancellationToken);
            if (resume.DeletedAt is null)
            {
                resume.Status = PracticeValues.Failed;
                resume.UpdatedAt = current.ProcessedAt.Value;
                EnqueueResourceChanged(resume.UserId, "resume", resume.Id, resume.Status, resume.UpdatedAt);
            }
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
        else if (current.Type == "InterviewAnswerEvaluationRequested")
        {
            var answer = await dbContext.InterviewAnswers.SingleOrDefaultAsync(item => item.Id == current.AggregateId, cancellationToken);
            if (answer is not null && answer.EvaluationStatus != InterviewAnswerEvaluationStates.Ready)
            {
                answer.EvaluationStatus = InterviewAnswerEvaluationStates.Failed;
                answer.Evaluation = null;
                answer.EvaluationErrorCode = EvaluationErrorCode(exception);
                answer.EvaluationCompletedAt = current.ProcessedAt;
                EnqueueResourceChanged(answer.UserId, "interview", answer.InterviewSessionId, answer.EvaluationStatus, current.ProcessedAt.Value);
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
        if (resumeId is not null && !await dbContext.Resumes.AnyAsync(item =>
                item.Id == resumeId && item.UserId == userId && item.DeletedAt == null && item.Status == PracticeValues.Ready,
                cancellationToken))
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
            .FirstOrDefault(item => item.ReleasedAt is not null &&
                session.Answers.All(existing => existing.QuestionId != item.Id));
        var continuation = await BuildContinuationAsync(userId, session, cancellationToken);
        var isComplete = next is null && continuation?.State == InterviewContinuationValues.MaxQuestionsReached;
        return new AnswerResult(MapAnswer(answer, session.Status), next is null ? null : MapQuestion(next), isComplete, continuation);
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

    private Task<bool> HasPendingQuestionPlanJobAsync(Guid interviewId, CancellationToken cancellationToken) =>
        dbContext.OutboxEvents.AnyAsync(item => item.AggregateId == interviewId &&
            item.Type == "InterviewQuestionPlanRequested" &&
            (item.Status == BillingValues.Pending || item.Status == BillingValues.Processing), cancellationToken);

    private async Task<(Guid Id, string Status, DateTimeOffset CreatedAt)?> GetLatestQuestionPlanJobAsync(
        Guid interviewId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.OutboxEvents.AsNoTracking()
            .Where(item => item.AggregateId == interviewId && item.Type == "InterviewQuestionPlanRequested")
            .Select(item => new { item.Id, item.Status, item.CreatedAt })
            .ToArrayAsync(cancellationToken);
        var latest = rows
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();
        return latest is null ? null : (latest.Id, latest.Status, latest.CreatedAt);
    }

    private async Task<string> GetQuestionPreparationStateAsync(
        Guid userId,
        InterviewSession session,
        CancellationToken cancellationToken)
    {
        if (session.Status != PracticeValues.Active)
            return InterviewQuestionPreparationStates.Processing;

        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);
        var released = session.Questions.Where(item => item.ReleasedAt is not null).ToArray();
        var hasUnansweredReleasedQuestion = released.Any(item =>
            session.Answers.All(answer => answer.QuestionId != item.Id));
        if (hasUnansweredReleasedQuestion || session.Questions.Count >= questionLimit)
            return InterviewQuestionPreparationStates.Ready;

        if (await HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
            return InterviewQuestionPreparationStates.Processing;

        var latestPlan = await GetLatestQuestionPlanJobAsync(session.Id, cancellationToken);
        return latestPlan?.Status == BillingValues.Failed
            ? InterviewQuestionPreparationStates.Failed
            : InterviewQuestionPreparationStates.Processing;
    }

    private async Task TryQueueReportIfReadyAsync(
        Guid interviewId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var session = await dbContext.InterviewSessions
            .SingleOrDefaultAsync(item => item.Id == interviewId, cancellationToken);
        if (session?.Status != PracticeValues.Completing)
            return;

        var answerStates = await dbContext.InterviewAnswers.AsNoTracking()
            .Where(item => item.InterviewSessionId == interviewId)
            .Select(item => item.EvaluationStatus)
            .ToArrayAsync(cancellationToken);
        if (answerStates.Length == 0 || answerStates.Any(state => state != InterviewAnswerEvaluationStates.Ready) ||
            await dbContext.InterviewReports.AnyAsync(item => item.InterviewSessionId == interviewId, cancellationToken) ||
            await HasPendingReportJobAsync(interviewId, cancellationToken))
            return;

        dbContext.Add(Outbox("InterviewReportRequested", "interview", interviewId, now));
    }

    private static string[]? ReadMissingStarElements(string? evaluation)
    {
        if (string.IsNullOrWhiteSpace(evaluation)) return null;
        try
        {
            using var document = JsonDocument.Parse(evaluation);
            if (!document.RootElement.TryGetProperty("star", out var star) ||
                !star.TryGetProperty("missingElements", out var missing) ||
                missing.ValueKind != JsonValueKind.Array)
                return null;
            return missing.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string EvaluationErrorCode(Exception exception) => exception switch
    {
        BusinessException { Code: "AI_PROVIDER_AUTH_FAILED" } => "AI_PROVIDER_AUTH_FAILED",
        BusinessException { Code: "AI_PROVIDER_CONFIGURATION_INVALID" } => "AI_PROVIDER_CONFIGURATION_INVALID",
        BusinessException { Code: "AI_RATE_LIMITED" } => "AI_RATE_LIMITED",
        BusinessException { Code: "AI_PROVIDER_UNAVAILABLE" } => "AI_PROVIDER_UNAVAILABLE",
        BusinessException { Code: "AI_OUTPUT_INVALID" } => "AI_OUTPUT_INVALID",
        AiProviderException { Kind: AiProviderFailureKind.Authentication } => "AI_PROVIDER_AUTH_FAILED",
        AiProviderException { Kind: AiProviderFailureKind.Configuration } => "AI_PROVIDER_CONFIGURATION_INVALID",
        AiProviderException { Kind: AiProviderFailureKind.RateLimited } => "AI_RATE_LIMITED",
        AiProviderException { Kind: AiProviderFailureKind.Timeout or AiProviderFailureKind.Unavailable } => "AI_PROVIDER_UNAVAILABLE",
        _ => "INTERNAL_PROCESSING_FAILED"
    };

    private static int PreparationLimit(int questionLimit, int startSequence = 1) =>
        questionLimit == int.MaxValue
            ? startSequence + UnlimitedQuestionPlanBatchSize - 1
            : Math.Max(InterviewQuestionValues.FreeQuestionLimit, questionLimit);

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

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null) throw NotFound();
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

    private sealed record InterviewStartContext(
        string Role,
        string Seniority,
        string InterviewType,
        string Difficulty,
        Guid? ResumeId,
        Guid? JobDescriptionId,
        Guid? CareerGoalId,
        Guid? SourceInterviewId,
        Guid? SourceQuestionId,
        string? PracticeReason,
        string? FocusTopic);

    private sealed record PreparedInterviewQuestion(
        int Sequence,
        string Topic,
        string Content,
        string PromptVersion,
        string ModelVersion);

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    private static AiExecutionResult<InterviewReportOutput>? BuildDeterministicReportFallback(
        InterviewQuestion[] answeredQuestions,
        AiOperationContext context)
    {
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
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(evidence))
                return null;
            scores.Add(new RubricScore(criterion, aggregate, evidence));
        }

        var strengths = evaluations
            .OrderBy(item => item.Question.Sequence)
            .ThenBy(item => item.Answer.Id)
            .SelectMany(item => item.Evaluation!.Strengths ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => item.Length is > 0 and <= 500)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        if (strengths.Length == 0)
            return null;

        var improvements = evaluations
            .OrderBy(item => item.Question.Sequence)
            .ThenBy(item => item.Answer.Id)
            .SelectMany(item => item.Evaluation!.Improvements ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => item.Length is > 0 and <= 500)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        if (improvements.Length == 0)
            return null;

        var fallback = new InterviewReportOutput(
            scores,
            strengths,
            improvements,
            improvements,
            AiOperations.ScoreScale);
        var validation = AiOperations.InterviewReport.NormalizeAndValidate(fallback, context);
        if (!validation.IsValid)
            return null;

        return new AiExecutionResult<InterviewReportOutput>(
            validation.NormalizedValue!,
            "deterministic:validated-answer-aggregate-v1",
            "interview-report-fallback-v1",
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

    [LoggerMessage(LogLevel.Warning,
        "Interview evaluation failed. purpose={Purpose} model={ModelVersion} answerId={AnswerId} interviewId={InterviewId} failureKind={FailureKind} requestId={RequestId}")]
    private static partial void EvaluationFailed(
        ILogger logger, string purpose, string modelVersion, Guid answerId, Guid interviewId, string failureKind, string requestId);

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

    [LoggerMessage(LogLevel.Warning,
        "Resume storage cleanup failed for resume {ResumeId}; retry scheduled after attempt {AttemptNumber}.")]
    private static partial void ResumeStorageCleanupFailed(ILogger logger, Guid resumeId, int attemptNumber);

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
        InterviewContinuationView? continuation = null,
        string reportState = InterviewReportStates.None,
        string resultState = InterviewResultStates.Collecting,
        InterviewEvaluationProgress? evaluationProgress = null,
        string questionPreparationState = InterviewQuestionPreparationStates.Processing) =>
        new(session.Id, session.Status, session.Role, session.Seniority, session.InterviewType, session.Difficulty, session.Version,
            questions.Where(item => item.ReleasedAt is not null).OrderBy(item => item.Sequence).Select(MapQuestion).ToArray(),
            answers.OrderBy(item => item.CreatedAt).Select(answer => MapAnswer(answer, session.Status)).ToArray(),
            session.CreatedAt, session.UpdatedAt, continuation, reportState, resultState,
            evaluationProgress ?? BuildEvaluationProgress(answers), questionPreparationState);

    private static InterviewEvaluationProgress BuildEvaluationProgress(IEnumerable<InterviewAnswer> answers)
    {
        var states = answers.Select(item => item.EvaluationStatus).ToArray();
        return new(
            states.Length,
            states.Count(state => state == InterviewAnswerEvaluationStates.Queued),
            states.Count(state => state == InterviewAnswerEvaluationStates.Processing),
            states.Count(state => state == InterviewAnswerEvaluationStates.Ready),
            states.Count(state => state == InterviewAnswerEvaluationStates.Failed));
    }

    private static string GetResultState(
        string status,
        string reportState,
        InterviewEvaluationProgress progress) =>
        status == PracticeValues.Active
                ? InterviewResultStates.Collecting
                : progress.Failed > 0 || reportState == InterviewReportStates.Failed
                    ? InterviewResultStates.Failed
                : status == PracticeValues.Completed && reportState == InterviewReportStates.Ready
                    ? InterviewResultStates.Ready
                    : InterviewResultStates.Processing;

    private async Task<string> GetReportStateAsync(
        Guid interviewId,
        string interviewStatus,
        CancellationToken cancellationToken)
    {
        if (await dbContext.InterviewReports.AsNoTracking()
                .AnyAsync(item => item.InterviewSessionId == interviewId, cancellationToken))
            return InterviewReportStates.Ready;

        var latestJob = (await dbContext.OutboxEvents.AsNoTracking()
                .Where(item => item.AggregateId == interviewId && item.Type == "InterviewReportRequested")
                .Select(item => new { item.Id, item.Status, item.CreatedAt })
                .ToArrayAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();
        if (latestJob?.Status == BillingValues.Failed)
            return InterviewReportStates.Failed;
        if (string.Equals(interviewStatus, PracticeValues.Completing, StringComparison.Ordinal) &&
            latestJob?.Status is BillingValues.Pending or BillingValues.Processing)
            return InterviewReportStates.Processing;
        return InterviewReportStates.None;
    }

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

        var questions = session.Questions.Count(item => item.ReleasedAt is not null);
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
    private static AnswerView MapAnswer(InterviewAnswer answer, string interviewStatus = PracticeValues.Completed) => new(
        answer.Id,
        answer.QuestionId,
        answer.Content,
        answer.DurationSeconds,
        interviewStatus == PracticeValues.Active && answer.EvaluationStatus == InterviewAnswerEvaluationStates.Ready
            ? null
            : answer.EvaluationStatus == InterviewAnswerEvaluationStates.Ready
                ? ParseJson(answer.Evaluation)
                : null,
        answer.CreatedAt,
        answer.EvaluationStatus);
    private static ReportView MapReport(InterviewReport report, IEnumerable<InterviewQuestion> questions, IEnumerable<InterviewAnswer> answers)
    {
        var orderedQuestions = questions.Where(item => item.ReleasedAt is not null).OrderBy(item => item.Sequence).ToArray();
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
                    evaluation?.ImprovedAnswer,
                    evaluation?.SampleAnswer);
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

    private async Task<ResolvedResumeAnalysisCommand> ResolveResumeAnalysisCommandAsync(
        Guid userId,
        StartResumeAnalysisCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ResumeId == Guid.Empty)
            throw Validation("Resume không hợp lệ.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        var normalized = NormalizeAnalysisCommand(command);
        ResumeAnalysisGoalContext? goal = null;
        if (command.CareerGoalId is { } careerGoalId)
        {
            goal = await dbContext.CareerGoals.AsNoTracking()
                .Where(item => item.Id == careerGoalId && item.UserId == userId && item.DeletedAt == null)
                .Select(item => new ResumeAnalysisGoalContext(
                    item.TargetRole,
                    item.Seniority,
                    item.Industry,
                    item.TargetJobDescriptionId))
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw CareerGoalNotFound();
        }
        else
        {
            goal = await dbContext.CareerGoals.AsNoTracking()
                .Where(item => item.UserId == userId && item.Active && item.DeletedAt == null)
                .Select(item => new ResumeAnalysisGoalContext(
                    item.TargetRole,
                    item.Seniority,
                    item.Industry,
                    item.TargetJobDescriptionId))
                .SingleOrDefaultAsync(cancellationToken);
        }

        var resumeId = command.ResumeId;
        if (resumeId is null)
        {
            var primaryResumeId = await dbContext.UserProfiles.AsNoTracking()
                .Where(item => item.UserId == userId)
                .Select(item => item.PrimaryResumeId)
                .SingleOrDefaultAsync(cancellationToken);
            if (primaryResumeId is { } selectedResumeId &&
                await dbContext.Resumes.AsNoTracking().AnyAsync(item =>
                    item.Id == selectedResumeId && item.UserId == userId && item.DeletedAt == null,
                    cancellationToken))
                resumeId = selectedResumeId;
            if (resumeId is null || resumeId == Guid.Empty)
                throw NotFound();
        }

        if (normalized.Mode == ResumeAnalysisMode.JobTargeted)
        {
            var jobDescriptionId = normalized.JobDescriptionId ?? goal?.TargetJobDescriptionId;
            if (jobDescriptionId is null)
                throw Validation("Job-targeted analysis yêu cầu JobDescription và không nhận ngữ cảnh benchmark.", "RESUME_ANALYSIS_CONTEXT_INVALID");

            return new(
                normalized.Mode,
                normalized.ModeWire,
                resumeId.Value,
                jobDescriptionId,
                null,
                null,
                null);
        }

        var industry = normalized.Industry ?? NormalizeOptional(goal?.Industry, 160);
        var targetRole = normalized.TargetRole ?? NormalizeOptional(goal?.TargetRole, 160);
        var seniority = normalized.Seniority ?? NormalizeOptional(goal?.Seniority, 80);
        if (industry is null || targetRole is null || seniority is null)
            throw Validation("Field-benchmark analysis yêu cầu industry, targetRole và seniority; không nhận JobDescription.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        return new(
            normalized.Mode,
            normalized.ModeWire,
            resumeId.Value,
            null,
            industry,
            targetRole,
            seniority);
    }

    private static NormalizedResumeAnalysisCommand NormalizeAnalysisCommand(StartResumeAnalysisCommand command)
    {
        if (!ResumeAnalysisModes.TryParse(command.Mode, out var mode))
            throw Validation("Mode phân tích CV không hợp lệ.", "RESUME_ANALYSIS_MODE_INVALID");

        var industry = NormalizeOptional(command.Industry, 160);
        var targetRole = NormalizeOptional(command.TargetRole, 160);
        var seniority = NormalizeOptional(command.Seniority, 80);
        if (command.Industry is not null && industry is null ||
            command.TargetRole is not null && targetRole is null ||
            command.Seniority is not null && seniority is null)
            throw Validation("Ngữ cảnh phân tích CV quá dài.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        if (mode == ResumeAnalysisMode.JobTargeted && (industry is not null || targetRole is not null || seniority is not null))
            throw Validation("Job-targeted analysis yêu cầu JobDescription và không nhận ngữ cảnh benchmark.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        if (mode == ResumeAnalysisMode.FieldBenchmark && command.JobDescriptionId is not null)
            throw Validation("Field-benchmark analysis yêu cầu industry, targetRole và seniority; không nhận JobDescription.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        return new(mode, mode.ToWireValue(), command.JobDescriptionId, industry, targetRole, seniority);
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

    private sealed record ResolvedResumeAnalysisCommand(
        ResumeAnalysisMode Mode,
        string ModeWire,
        Guid ResumeId,
        Guid? JobDescriptionId,
        string? Industry,
        string? TargetRole,
        string? Seniority);

    private sealed record ResumeAnalysisGoalContext(
        string TargetRole,
        string Seniority,
        string? Industry,
        Guid? TargetJobDescriptionId);

    private static BusinessException Validation(string message, string code = "VALIDATION_ERROR") => new(code, message, BusinessErrorKind.Validation);
    private static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    private static BusinessException CareerGoalNotFound() => new("CAREER_GOAL_NOT_FOUND", "Không tìm thấy career goal.", BusinessErrorKind.NotFound);
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
