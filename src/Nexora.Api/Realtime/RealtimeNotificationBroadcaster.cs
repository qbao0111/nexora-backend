using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexora.Data.Persistence;

namespace Nexora.Api.Realtime;

// One API instance only. Successful sends are not client acknowledgements; reconnect
// reconciliation and eventId deduplication remain necessary on the frontend.
public sealed partial class RealtimeNotificationBroadcaster(
    IServiceScopeFactory scopeFactory,
    IHubContext<RealtimeHub> hubContext,
    IOptions<RealtimeOptions> options,
    TimeProvider timeProvider,
    ILogger<RealtimeNotificationBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.Value.IdleDelayMilliseconds;
            try
            {
                if (await BroadcastPendingAsync(stoppingToken) > 0)
                    delay = options.Value.BusyDelayMilliseconds;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Never log exceptions: database/transport errors may contain sensitive data.
                PollFailed(logger);
                delay = options.Value.FailureDelayMilliseconds;
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<int> BroadcastPendingAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) return 0;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = timeProvider.GetUtcNow();
        var pending = db.RealtimeNotifications.Where(item => item.ProcessedAt == null);
        // SQLite is used only by deterministic integration tests and cannot order DateTimeOffset.
        var batch = db.Database.IsNpgsql()
            ? await pending.Where(item => item.NextAttemptAt == null || item.NextAttemptAt <= now)
                .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
                .Take(options.Value.BatchSize).ToArrayAsync(cancellationToken)
            : (await pending.ToArrayAsync(cancellationToken))
                .Where(item => item.NextAttemptAt == null || item.NextAttemptAt <= now)
                .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Take(options.Value.BatchSize).ToArray();

        foreach (var notification in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await hubContext.Clients.User(notification.UserId.ToString()).SendAsync(
                    ResourceChangedEvent.Name,
                    new ResourceChangedEvent(notification.Id, notification.ResourceType,
                        notification.ResourceId, notification.Status, notification.CreatedAt), cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                notification.Attempts++;
                notification.NextAttemptAt = timeProvider.GetUtcNow().AddMilliseconds(options.Value.FailureDelayMilliseconds);
                await db.SaveChangesAsync(cancellationToken);
                SendFailed(logger, notification.Id);
                continue;
            }

            notification.ProcessedAt = timeProvider.GetUtcNow();
            notification.NextAttemptAt = null;
            // If this write fails, the same eventId may be sent again, without business effects.
            await db.SaveChangesAsync(cancellationToken);
        }
        return batch.Length;
    }

    [LoggerMessage(LogLevel.Warning, "Realtime notification polling failed; retry scheduled")]
    private static partial void PollFailed(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Realtime notification {EventId} send failed; retry scheduled")]
    private static partial void SendFailed(ILogger logger, Guid eventId);
}
