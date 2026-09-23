using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Billing;
using Nexora.Business.Storage;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class ResumeStorageCleanupProcessor(
    NexoraDbContext dbContext,
    IStorageProvider storageProvider,
    TimeProvider timeProvider,
    ILogger<PracticeService> logger)
{
    internal async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
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

    private sealed record ResumeStorageCleanupCandidate(Guid ResumeId, int Attempts, DateTimeOffset? NextAttemptAt);

    [LoggerMessage(LogLevel.Warning,
        "Resume storage cleanup failed for resume {ResumeId}; retry scheduled after attempt {AttemptNumber}.")]
    private static partial void ResumeStorageCleanupFailed(ILogger logger, Guid resumeId, int attemptNumber);
}
