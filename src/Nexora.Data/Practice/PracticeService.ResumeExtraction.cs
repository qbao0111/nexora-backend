using System.Security.Cryptography;
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

}
