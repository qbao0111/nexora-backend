using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Business.Ai;
using Nexora.Business.Common;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class ProviderAdmission(
    IServiceScopeFactory scopeFactory, IOptions<ProviderBudgetOptions> options,
    TimeProvider timeProvider, ILogger<ProviderAdmission> logger) : IProviderAdmission
{
    // PostgreSQL serializes admission across processes. This gate is only for
    // deterministic SQLite tests, which cannot take PostgreSQL advisory locks.
    private static readonly SemaphoreSlim TestGate = new(1, 1);

    public async Task<Guid> ReserveAsync(ProviderAdmissionRequest request, CancellationToken cancellationToken)
    {
        var limits = options.Value;
        if (request.Attempt is < 1 or > 2 || request.UserId == Guid.Empty || request.JobId == Guid.Empty ||
            request.ReservedTokens <= 0 || request.ReservedTokens > limits.MaximumInputBytes + 8192L + 1024)
            throw Denied("context_or_input");
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var postgres = db.Database.IsNpgsql();
        if (!postgres) await TestGate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (postgres)
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(782346109)", cancellationToken);
            var now = timeProvider.GetUtcNow();
            var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var hour = now.AddHours(-1);
            if (await db.ProviderCallReservations.AnyAsync(item => item.JobId == request.JobId &&
                item.Purpose == request.Purpose && item.OperationKey == request.OperationKey &&
                item.Attempt == request.Attempt, cancellationToken))
                throw Denied("attempt_already_authorized");

            // Only operational IDs and bounded counters are read, never candidate text.
            // SQLite cannot translate DateTimeOffset comparison; PostgreSQL stays indexed.
            var query = db.ProviderCallReservations.AsNoTracking().Select(item => new
            {
                item.UserId, item.Purpose, item.OperationKey, item.StartedAt, item.CompletedAt,
                item.LeaseExpiresAt, item.CooldownUntil, item.ReservedTokens
            });
            var records = postgres
                ? await query.Where(item => item.StartedAt >= day || item.StartedAt >= hour ||
                    (item.CompletedAt == null && item.LeaseExpiresAt > now)).ToArrayAsync(cancellationToken)
                : await query.ToArrayAsync(cancellationToken);
            var daily = records.Where(item => item.StartedAt >= day).ToArray();
            var userDaily = daily.Where(item => item.UserId == request.UserId).ToArray();
            var userHourly = records.Where(item => item.UserId == request.UserId && item.StartedAt >= hour).ToArray();
            if (request.Purpose != "speech.token" && records.Any(item => item.Purpose != "speech.token" && item.CooldownUntil > now))
                throw Denied("provider_cooldown");
            var purposeLimit = limits.PurposeHourlyCalls.GetValueOrDefault(request.Purpose, limits.UserHourlyCalls);
            if (request.Purpose == "speech.token")
            {
                // An Azure STS token remains usable upstream for up to 10 minutes.
                // Count distinct issued session identities, not claims of revocation.
                var active = userHourly.Where(item => item.Purpose == "speech.token" && item.StartedAt > now.AddMinutes(-10))
                    .Select(item => item.OperationKey).Distinct(StringComparer.Ordinal).ToArray();
                if (!active.Contains(request.OperationKey, StringComparer.Ordinal) && active.Length >= limits.MaximumSpeechSessions)
                    throw Denied("speech_sessions");
            }
            if (daily.Length >= limits.GlobalDailyCalls || userDaily.Length >= limits.UserDailyCalls ||
                userHourly.Length >= limits.UserHourlyCalls ||
                userHourly.Count(item => item.Purpose == request.Purpose) >= purposeLimit ||
                daily.Sum(item => item.ReservedTokens) + request.ReservedTokens > limits.GlobalDailyTokens ||
                userDaily.Sum(item => item.ReservedTokens) + request.ReservedTokens > limits.UserDailyTokens ||
                userHourly.Sum(item => item.ReservedTokens) + request.ReservedTokens > limits.UserHourlyTokens)
                throw Denied("budget");
            if (records.Count(item => item.CompletedAt == null && item.LeaseExpiresAt > now) >= limits.GlobalInFlight)
                throw Denied("concurrency");
            var reservation = new ProviderCallReservation
            {
                UserId = request.UserId, JobId = request.JobId, Purpose = request.Purpose,
                OperationKey = request.OperationKey, Attempt = request.Attempt,
                ReservedTokens = request.ReservedTokens, StartedAt = now,
                // Provider timeout is capped at 60s; leave room for transport cleanup.
                // Expiry frees concurrency only, NEVER call/token budget or replay rights.
                LeaseExpiresAt = now.AddMinutes(3)
            };
            db.ProviderCallReservations.Add(reservation);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Admitted(logger, request.Purpose, request.JobId, request.Attempt, request.ReservedTokens);
            return reservation.Id;
        }
        finally { if (!postgres) TestGate.Release(); }
    }

    public async Task<string?> ReadResultAsync(ProviderAdmissionRequest request, string fingerprint, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var record = await db.ProviderCallReservations.AsNoTracking().SingleOrDefaultAsync(item =>
            item.UserId == request.UserId && item.JobId == request.JobId && item.Purpose == request.Purpose &&
            item.OperationKey == request.OperationKey && item.Attempt == request.Attempt, cancellationToken);
        if (record?.ResultFingerprint is not null && record.ResultFingerprint != fingerprint) throw Denied("checkpoint_context_changed");
        if (record?.CompletedAt is not null && Enum.TryParse<AiProviderFailureKind>(record.FailureKind, out var failure))
            throw new AiProviderException(failure, "Previously recorded provider failure.",
                retryHint: Enum.TryParse<AiProviderRetryHint>(record.FailureRetryHint, out var hint) ? hint : AiProviderRetryHint.None);
        if (record?.ResultJson is null) return null;
        if (record.ResultFingerprint != fingerprint) throw Denied("checkpoint_context_changed");
        return record.ResultJson;
    }

    public Task CompleteAsync(Guid reservationId, AiTokenUsage? usage, CancellationToken cancellationToken, AiProviderFailureKind? failureKind = null) =>
        ReconcileCompletionAsync(reservationId, usage, failureKind, null, null, null, cancellationToken);

    public Task CompleteResultAsync(Guid reservationId, string fingerprint, string resultJson, AiTokenUsage? usage, CancellationToken cancellationToken) =>
        ReconcileCompletionAsync(reservationId, usage, null, fingerprint, resultJson, null, cancellationToken);

    public Task CompleteFailureAsync(Guid reservationId, string fingerprint, AiTokenUsage? usage, AiProviderFailureKind failureKind, AiProviderRetryHint retryHint, CancellationToken cancellationToken) =>
        ReconcileCompletionAsync(reservationId, usage, failureKind, fingerprint, null, retryHint, cancellationToken);

    private async Task ReconcileCompletionAsync(Guid reservationId, AiTokenUsage? usage, AiProviderFailureKind? failureKind,
        string? fingerprint, string? resultJson, AiProviderRetryHint? retryHint, CancellationToken cancellationToken)
    {
        var backoff = new DatabaseOutageBackoff(timeProvider);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await CompleteOnceAsync(reservationId, usage, failureKind, fingerprint, resultJson, retryHint, timeout.Token);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                (DatabaseConnectivityFailure.IsTransient(exception) || exception is OperationCanceledException && timeout.IsCancellationRequested))
            {
                var (delay, report) = backoff.Next(Random.Shared.NextDouble());
                if (report) CompletionDeferred(logger, reservationId);
                // DB reconciliation only. Keep the successful response in memory;
                // never re-enter GenerateStructuredAsync while storage is unavailable.
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }
    }

    private async Task CompleteOnceAsync(Guid reservationId, AiTokenUsage? usage, AiProviderFailureKind? failureKind,
        string? fingerprint, string? resultJson, AiProviderRetryHint? retryHint, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ownerId = await db.ProviderCallReservations.Where(item => item.Id == reservationId).Select(item => item.UserId).SingleAsync(cancellationToken);
        var suppressResult = false;
        if (resultJson is not null || fingerprint is not null)
        {
            // Lock owner BEFORE ledger, matching privacy purge lock order.
            var user = db.Database.IsNpgsql()
                ? await db.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {ownerId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken)
                : await db.Users.SingleOrDefaultAsync(item => item.Id == ownerId, cancellationToken);
            suppressResult = user?.DeletionRequestedAt is not null || user?.DeletedAt is not null;
        }
        var reservation = db.Database.IsNpgsql()
            ? await db.ProviderCallReservations.FromSqlInterpolated(
                $"SELECT * FROM provider_call_reservations WHERE \"Id\" = {reservationId} FOR UPDATE").SingleAsync(cancellationToken)
            : await db.ProviderCallReservations.SingleAsync(item => item.Id == reservationId, cancellationToken);
        if (reservation.CompletedAt is not null) return;
        // A late response cannot recreate private content after account deletion.
        reservation.ResultJson = suppressResult ? null : resultJson;
        reservation.ResultFingerprint = suppressResult ? null : fingerprint;
        reservation.CompletedAt = timeProvider.GetUtcNow();
        reservation.FailureKind = failureKind?.ToString();
        reservation.FailureRetryHint = retryHint?.ToString();
        if (failureKind is AiProviderFailureKind.RateLimited or AiProviderFailureKind.Unavailable or AiProviderFailureKind.Timeout)
        {
            // Do not suppress the established initial + repair contract after
            // one failed call. Open only for a cluster of transport failures.
            var recent = timeProvider.GetUtcNow().AddMinutes(-1);
            var failedQuery = db.ProviderCallReservations.AsNoTracking()
                .Where(item => item.FailureKind == "RateLimited" || item.FailureKind == "Unavailable" || item.FailureKind == "Timeout");
            if (db.Database.IsNpgsql()) failedQuery = failedQuery.Where(item => item.CompletedAt >= recent && item.StartedAt >= recent.AddMinutes(-3));
            var failures = await failedQuery.Select(item => item.CompletedAt).ToArrayAsync(cancellationToken);
            if (failures.Count(completed => completed >= recent) + 1 >= options.Value.CooldownFailureThreshold)
                reservation.CooldownUntil = timeProvider.GetUtcNow().AddSeconds(options.Value.CooldownSeconds);
        }
        if (usage is { PromptTokens: >= 0, CompletionTokens: >= 0 })
        {
            reservation.ActualPromptTokens = usage.PromptTokens;
            reservation.ActualCompletionTokens = usage.CompletionTokens;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private BusinessException Denied(string reason)
    {
        Rejected(logger, reason);
        return new BusinessException("AI_ADMISSION_DENIED", "Tác vụ AI tạm thời bị giới hạn. Vui lòng thử lại sau.", BusinessErrorKind.RateLimited);
    }

    [LoggerMessage(LogLevel.Information, "Provider admission purpose={Purpose} job={JobId} attempt={Attempt} reservedTokens={Tokens}")]
    private static partial void Admitted(ILogger logger, string purpose, Guid jobId, int attempt, long tokens);
    [LoggerMessage(LogLevel.Warning, "Provider admission rejected reason={Reason}")]
    private static partial void Rejected(ILogger logger, string reason);
    [LoggerMessage(LogLevel.Warning, "Provider result checkpoint deferred by database outage. reservation={ReservationId}; no additional provider call")]
    private static partial void CompletionDeferred(ILogger logger, Guid reservationId);
}
