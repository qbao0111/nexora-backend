using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Business.Privacy;
using Nexora.Data.Persistence;

namespace Nexora.Data.Privacy;

public sealed partial class RetentionProcessor(
    NexoraDbContext db, IOptions<RetentionOptions> options,
    TimeProvider timeProvider, ILogger<RetentionProcessor> logger) : IRetentionProcessor
{
    // Also required by the documented operator legal-hold transaction.
    public const long AdvisoryLockKey = 761604031;
    private readonly RetentionOptions settings = options.Value;

    public async Task<RetentionRunResult> RunDueAsync(CancellationToken cancellationToken)
    {
        var dryRun = settings.DryRun || !settings.PurgeEnabled;
        if (!settings.Enabled) return new("disabled", dryRun);
        // Never approximate PostgreSQL coordination with an in-memory lock.
        if (!db.Database.IsNpgsql()) return new("unsupported_database", dryRun);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var acquired = await db.Database.SqlQueryRaw<bool>(
            "SELECT pg_try_advisory_xact_lock(761604031) AS \"Value\"").SingleAsync(cancellationToken);
        if (!acquired) return new("locked", dryRun);
        var checkpoint = await db.RetentionCheckpoints.SingleAsync(item => item.Id == 1, cancellationToken);
        if (checkpoint.ConsecutiveFailures >= settings.MaxConsecutiveFailures) return new("suspended", dryRun);
        if (checkpoint.NextRunAt > now) return new("not_due", dryRun);

        await transaction.CreateSavepointAsync("retention_batch", cancellationToken);
        var examined = 0;
        var eligible = 0;
        var skipped = 0;
        try
        {
            var expiryCutoff = now.AddHours(-settings.VerificationGraceHours);
            var auditCutoff = now.AddMonths(-settings.AuditRetentionMonths);
            var verifications = db.ExternalDeletionVerifications.Where(item => item.ExpiresAt <= expiryCutoff);
            var eligibleVerifications = verifications.Where(item => !db.RetentionHolds.Any(hold =>
                hold.ReleasedAt == null && (hold.UserId == null || hold.UserId == item.UserId)));
            var verificationIds = await eligibleVerifications.OrderBy(item => item.ExpiresAt).ThenBy(item => item.Id)
                .Select(item => item.Id).Take(settings.BatchSize).ToArrayAsync(cancellationToken);
            skipped += await verifications.Where(item => db.RetentionHolds.Any(hold =>
                    hold.ReleasedAt == null && (hold.UserId == null || hold.UserId == item.UserId)))
                .OrderBy(item => item.ExpiresAt).ThenBy(item => item.Id).Take(settings.BatchSize).CountAsync(cancellationToken);

            var requests = db.DataPrivacyRequests.Where(item => item.Type == "account_deletion" &&
                item.Status == PrivacyValues.Completed && item.CompletedAt != null && item.CompletedAt <= auditCutoff);
            var eligibleRequests = requests.Where(item => db.Users.Any(user => user.Id == item.UserId &&
                    user.DeletedAt != null && user.DeletedAt <= auditCutoff) &&
                !db.DataPrivacyRequests.Any(other => other.UserId == item.UserId && other.Status != PrivacyValues.Completed) &&
                !db.RetentionHolds.Any(hold => hold.ReleasedAt == null && (hold.UserId == null || hold.UserId == item.UserId)));
            var requestIds = await eligibleRequests.OrderBy(item => item.CompletedAt).ThenBy(item => item.Id)
                .Select(item => item.Id).Take(settings.BatchSize).ToArrayAsync(cancellationToken);
            skipped += await requests.Where(item => !db.Users.Any(user => user.Id == item.UserId &&
                    user.DeletedAt != null && user.DeletedAt <= auditCutoff) ||
                db.DataPrivacyRequests.Any(other => other.UserId == item.UserId && other.Status != PrivacyValues.Completed) ||
                db.RetentionHolds.Any(hold => hold.ReleasedAt == null && (hold.UserId == null || hold.UserId == item.UserId)))
                .OrderBy(item => item.CompletedAt).ThenBy(item => item.Id).Take(settings.BatchSize).CountAsync(cancellationToken);
            eligible = verificationIds.Length + requestIds.Length;
            examined = eligible + skipped;
            var removed = 0;
            if (!dryRun)
            {
                // Re-evaluate eligibility in each delete statement. Both categories
                // and the checkpoint commit together; failure rolls the whole batch back.
                removed += await eligibleVerifications.Where(item => verificationIds.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
                removed += await eligibleRequests.Where(item => requestIds.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
            }
            var result = new RetentionRunResult(dryRun ? "reported" : "completed", dryRun, examined, eligible, removed, skipped);
            Record(checkpoint, result, now, now.AddHours(settings.SweepIntervalHours));
            checkpoint.ConsecutiveFailures = 0;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            RunRecorded(logger, result.Status, dryRun, examined, eligible, removed, skipped, 0);
            return result;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Retain the transaction lock while rolling back partial deletes.
            await transaction.RollbackToSavepointAsync("retention_batch", cancellationToken);
            db.ChangeTracker.Clear();
            checkpoint = await db.RetentionCheckpoints.SingleAsync(item => item.Id == 1, cancellationToken);
            checkpoint.ConsecutiveFailures++;
            var result = new RetentionRunResult("failed", dryRun, examined, eligible, 0, skipped, 1);
            var delayMinutes = Math.Min(1440d, settings.FailureBackoffMinutes * Math.Pow(2, checkpoint.ConsecutiveFailures - 1));
            Record(checkpoint, result, now, now.AddMinutes(delayMinutes));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            RunFailed(logger, checkpoint.ConsecutiveFailures, exception.GetType().Name);
            RunRecorded(logger, result.Status, dryRun, examined, eligible, 0, skipped, 1);
            return result;
        }
    }

    private static void Record(RetentionCheckpoint checkpoint, RetentionRunResult result, DateTimeOffset now, DateTimeOffset nextRun)
    {
        checkpoint.LastRunAt = now;
        checkpoint.NextRunAt = nextRun;
        checkpoint.Status = result.Status;
        checkpoint.DryRun = result.DryRun;
        checkpoint.Examined = result.Examined;
        checkpoint.Eligible = result.Eligible;
        checkpoint.Removed = result.Removed;
        checkpoint.Skipped = result.Skipped;
        checkpoint.Failed = result.Failed;
    }

    [LoggerMessage(LogLevel.Information,
        "Retention run {Status}, dry-run {DryRun}: examined {Examined}, eligible {Eligible}, removed {Removed}, skipped {Skipped}, failed {Failed}")]
    private static partial void RunRecorded(ILogger logger, string status, bool dryRun, int examined, int eligible, int removed, int skipped, int failed);

    [LoggerMessage(LogLevel.Warning, "Retention batch rolled back at failure {FailureCount} with {ExceptionType}")]
    private static partial void RunFailed(ILogger logger, int failureCount, string exceptionType);
}
