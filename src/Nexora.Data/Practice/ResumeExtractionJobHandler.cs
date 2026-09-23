using System.Data;
using System.Security.Cryptography;
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

public sealed partial class ResumeExtractionJobHandler(
    NexoraDbContext dbContext,
    IStorageProvider storageProvider,
    IDetailedDocumentExtractor detailedDocumentExtractor,
    IDocumentOcrProvider documentOcrProvider,
    IAiProvider aiProvider,
    TimeProvider timeProvider,
    ILogger<PracticeService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string ProfilePromptVersion => AiOperations.ResumeProfile.PromptVersion;
    private static string ProfileSchemaVersion => AiOperations.ResumeProfile.SchemaVersion;
    private string CurrentModelVersion => string.IsNullOrWhiteSpace(aiProvider.ModelVersion)
        ? throw new InvalidOperationException("The configured AI provider must expose a model version.")
        : aiProvider.ModelVersion.Trim();

    private static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    private void MarkProcessed(OutboxEvent job) { job.Status = BillingValues.Processed; job.ProcessedAt = timeProvider.GetUtcNow(); }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null) throw NotFound();
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

    private void EnqueueResourceChanged(Guid userId, string resourceType, Guid resourceId, string status, DateTimeOffset occurredAt) =>
        dbContext.RealtimeNotifications.Add(new Nexora.Data.Realtime.RealtimeNotification
        {
            UserId = userId, ResourceType = resourceType, ResourceId = resourceId, Status = status, CreatedAt = occurredAt
        });

    internal Task ProcessAsync(OutboxEvent job, CancellationToken cancellationToken) => ExtractResumeAsync(job, cancellationToken);

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

        ResumeProfileProcessor.ValidateResumeProfile(fallback.Profile);
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

    internal async Task FailAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var current = await dbContext.OutboxEvents.SingleAsync(item => item.Id == job.Id, cancellationToken);
        current.Status = PracticeValues.Failed;
        current.ProcessedAt = timeProvider.GetUtcNow();
        var resume = await dbContext.Resumes.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
        await LockUserAsync(resume.UserId, cancellationToken);
        await dbContext.Entry(resume).ReloadAsync(cancellationToken);
        if (resume.DeletedAt is null)
        {
            resume.Status = PracticeValues.Failed;
            resume.UpdatedAt = current.ProcessedAt.Value;
            EnqueueResourceChanged(resume.UserId, "resume", resume.Id, resume.Status, resume.UpdatedAt);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

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

}
