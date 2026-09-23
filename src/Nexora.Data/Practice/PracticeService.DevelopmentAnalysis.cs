using System.Security.Cryptography;
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
        return new(ResumeService.MapResume(analysis.Resume), JobDescriptionService.MapJobDescription(analysis.JobDescription ?? throw Validation("JobDescription is required for development analysis.", "RESUME_ANALYSIS_CONTEXT_INVALID")), ResumeAnalysisService.MapAnalysis(analysis));
    }

}
