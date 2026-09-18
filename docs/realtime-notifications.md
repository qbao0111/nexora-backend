# Realtime resource notifications

SignalR is an optional invalidation channel. REST remains the only command/query
API and source of truth. This addition supports FR-AUTH-02/03, FR-CV-03/06,
FR-INT-02/04 and NFR-REL/OBS without changing their existing business rules.

## Connection and authentication

Connect to `/hubs/realtime` on the API origin (not below `/api/v1`). The hub is
authenticated and has no client-callable application methods or group subscription
methods. SignalR user identity is exclusively the JWT `sub` claim; broadcasts use
`Clients.User(userId.ToString())` and reach that user's connected tabs/devices only.

Existing issuer, audience, signature, expiry, security-stamp and active/deleted-user
validation also applies to hub authentication. Browser WebSockets/SSE may send
`access_token` in the query string, accepted only on the hub path or its path
segments. REST does not accept query tokens. Bearer headers continue to work.
Connections close when their access token expires; reconnect using a current
in-memory token. Existing WebSocket authentication is not continuously revalidated
against the database: stop the connection on logout/account change, and discard
the event deduplication cache when changing accounts.

The existing credentialed CORS allow-list remains applicable. API logging suppresses
Hosting request-start and HTTP connection transport logs below Warning; application
telemetry uses paths without query strings. Do not enable request/header/token
logging or URL query logging at a proxy. Never put refresh tokens in JavaScript.
See [Microsoft's SignalR authentication guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0).

## Event contract

Only server event: `resourceChanged`.

```json
{
  "eventId": "00000000-0000-0000-0000-000000000001",
  "resourceType": "interview",
  "resourceId": "00000000-0000-0000-0000-000000000002",
  "status": "active",
  "occurredAt": "2026-09-08T10:00:00+00:00"
}
```

All IDs are UUIDs; `occurredAt` is the persisted UTC transition time, not send time.
Payloads contain only these five fields, never owner ID/email, CV text/profile,
answers, evaluation, report, provider information or secrets.

| Resource type | Status | Canonical REST refetch |
| --- | --- | --- |
| `resume` | `ready`, `failed` | `GET /api/v1/resumes/{id}` |
| `resume` | `deleted` | `GET /api/v1/resumes/{id}` returns owner-scoped 404; prune the resume and also refetch `/api/v1/me/career-profile` to reconcile Primary Resume. |
| `resumeAnalysis` | `completed`, `failed` | `GET /api/v1/resume-analyses/{id}` |
| `interview` | `active`, `failed` | `GET /api/v1/interviews/{id}` |
| `interview` | `completed` | `GET /api/v1/interviews/{id}/report` (or interview state if that is the mounted view) |
| `scenarioAttempt` | `completed`, `failed` | `GET /api/v1/scenario-attempts/{id}` |
| `starAttempt` | `completed`, `failed` | `GET /api/v1/star-attempts/{id}` |

No events for uploaded, extracting, processing, starting or completing.
**Report generation failure deliberately emits no event.** The existing interview
remains `completing` with its existing free retry/credit behavior. `interview.failed`
only denotes failure to activate the interview. Use bounded fallback reconciliation
and the existing report retry UX for a report that remains unavailable.

## Frontend integration

Register the handler before starting the connection. The example assumes the
application supplies `apiGet`, `updateResourceCache`, `refreshTokenIfNeeded`,
`isRelevantToCurrentView`, `scheduleSlowReconciliation`, and
`refetchTransitionalResources`. `API_ORIGIN` does not include `/api/v1`.

```js
const connection = new signalR.HubConnectionBuilder()
  .withUrl(`${API_ORIGIN}/hubs/realtime`, {
    accessTokenFactory: async () => {
      await refreshTokenIfNeeded(); // shared REST refresh lock; token stays in memory
      return accessToken;
    },
    withCredentials: true
  })
  .withAutomaticReconnect()
  .build();

// Per authenticated account and browser tab; cap memory use.
const seen = new Set();
connection.on("resourceChanged", async event => {
  if (seen.has(event.eventId) || !isRelevantToCurrentView(event)) return;
  let path;
  const id = encodeURIComponent(event.resourceId);
  if (event.resourceType === "resume") path = `/resumes/${id}`;
  if (event.resourceType === "resumeAnalysis") path = `/resume-analyses/${id}`;
  if (event.resourceType === "interview") {
    path = event.status === "completed"
      ? `/interviews/${id}/report`
      : `/interviews/${id}`;
  }
  if (event.resourceType === "scenarioAttempt") path = `/scenario-attempts/${id}`;
  if (event.resourceType === "starAttempt") path = `/star-attempts/${id}`;
  if (!path) return;
  seen.add(event.eventId); // before awaiting: concurrent duplicate delivery is ignored
  if (seen.size > 1000) seen.delete(seen.values().next().value);
  try {
    const resource = await apiGet(path); // one canonical refetch per unique event
    updateResourceCache(path, resource);
  } catch {
    scheduleSlowReconciliation(path); // don't turn duplicate events into a retry loop
  }
});

connection.onreconnected(() => refetchTransitionalResources());
try {
  await connection.start();
  await refetchTransitionalResources(); // covers completion before initial connection
} catch {
  scheduleSlowReconciliation(); // automatic reconnect does not retry an initial start failure
}
```

Coalesce cache requests already in flight. Notifications may be delayed, duplicated
or arrive after a newer REST state; never replace canonical state using `event.status`.
For each unique event, invalidate/refetch the relevant mounted REST resource once.
Slow fallback polling (15–30 seconds with a bounded UI timeout) remains appropriate
for transitional resources, including when the hub is disabled/unavailable. Reconcile
on reconnect and when mounting a resource after a command: a fast job can finish
before the command response has registered its resource ID in the frontend.

## Persistence and delivery

Worker and API share PostgreSQL, not an in-process SignalR context. The worker adds a
`realtime_notifications` row in the same `SaveChanges`/transaction as the corresponding
ready/final state (including first question + quota consumption, or report + completion).
Notification insertion failure rolls back that state change. Network delivery happens
later and cannot change business data or quota.

The API-hosted broadcaster reads up to 50 due pending rows, sends the allowlisted
event to its owner, then writes `ProcessedAt`. Send failure increments `Attempts`
and sets `NextAttemptAt`; other rows in the batch can still proceed. Exceptions and
provider content are not persisted or logged. A crash or database failure after send
can result in a duplicate with the **same eventId**. This is retryable dispatch,
not exactly-once delivery or client acknowledgement. SignalR send can succeed with
no connected client; reconnect/REST reconciliation is therefore required. There is
no per-client replay endpoint.

This implementation supports **one API instance**. Do not run multiple broadcasters
or scale API replicas with this in-memory SignalR connection registry. Multi-instance
routing/coordination is outside this task; no Redis, broker or external SignalR service
is introduced. Worker has no SignalR dependency.

## Configuration and rollout

```json
"Realtime": {
  "Enabled": true,
  "BatchSize": 50,
  "BusyDelayMilliseconds": 200,
  "IdleDelayMilliseconds": 1500,
  "FailureDelayMilliseconds": 3000
}
```

Configuration belongs to the API. `Enabled=false` disables the hub endpoint and
broadcaster; REST and worker state transitions still work and still persist notifications.
Re-enabling dispatches the backlog, so FE must treat old events as invalidations only.
The pending-read index is `(ProcessedAt, CreatedAt)`. Completed rows remain delivery
metadata; automatic retention/purge is not added here. Monitor table growth and settle
cleanup periods through the existing retention process. Account deletion removes that
account's notification metadata along with its existing private-data cleanup.

Apply the new EF migration through the existing migration workflow **before** running
either updated process, even if realtime is disabled. No application startup migration,
production deployment or production provider change is part of this task.

## Verification

`RealtimeApiTests` uses actual authenticated SignalR JSON handshakes and WebSockets
through `WebApplicationFactory`, including owner isolation, header/query tokens,
revoked/disabled/deleted accounts, payload allow-list, duplicate dispatch, send retries,
disabled realtime and the hosted broadcaster → REST interview/report flow.
`RealtimeWorkerTests` covers local DOCX extraction/failure, analysis completion/failure,
activation failure and rollback when the notification write fails. Existing report-failure
tests assert that no report-failure event is emitted. All providers are deterministic
test doubles where needed; the normal integration database remains SQLite, so these
tests do not claim live PostgreSQL lock/load or external hosting evidence.
