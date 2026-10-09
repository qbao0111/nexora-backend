# Worker PostgreSQL connectivity incident — 2026-10-08

Status: investigation + proposed hotfix; not deployed. Evidence collected 2026-10-09 UTC.

## Evidence and confidence

- Audited remote main: `9ba2939e6a732409a599dd745be9a626f7702f3c`.
  Baseline Backend CI run `37324776473` succeeded.
- Operator-supplied Sentry event: Production / `dotnet-aspnetcore`,
  2026-10-08 20:04:19 Asia/Ho_Chi_Minh = **13:04:19 UTC**.
  Sanitized `InvalidOperationException: Unhandled worker exception`, nested
  `NpgsqlException` / `SocketException`; suspected privacy polling before AI work.
  This session has no Sentry event/Neon metrics connector. The raw event, socket
  error code, grouping, frequency, connection count and compute state are unverified.
  Sentry suspect-commit attribution does not establish causation.
- Approved read-only SSH inspection at approximately 2026-10-09 08:40 UTC:
  `nexora-production-nexora-1` healthy, restart count **0**, started
  2026-10-07T05:07:04.224523505Z; local live/readiness **200/200**; runtime UID 1654.
  Host: 3.8 GiB RAM / approximately 2.8 GiB available; approximately 18 GiB disk
  available. Container approximately 315 MiB RAM / 1.45% CPU at that instant.
- Retained Docker log query 2026-10-08 12:59:19–13:14:19 UTC returned no lines.
  Driver is json-file, three 10 MB files. Missing retained logs are **not** proof
  that no incident occurred. No historic restart/latency reconstruction is possible.
- Actual established connection peer to container port 10000 was **172.18.0.1**.
  This was observed from the live connection table, not inferred from Docker defaults.
  No `RateLimits__*`, `ReverseProxy__*`, or automatic forwarded-header override was
  found in the container environment. No secrets or candidate data were returned.

**Conclusion:** a database transport failure is consistent with the supplied
exception chain, but the exact cause is **unknown / low confidence**. DNS failure,
timeout/reset, TLS, Neon cold start/suspension, pool/max-connection exhaustion and
query failure cannot be distinguished from this evidence. No evidence links this
event to an attack, excess provider billing or container resource exhaustion.

## Impact and affected paths

`PracticeWorker.ExecuteAsync` calls `IPrivacyJobProcessor.ProcessPendingAsync`
before practice and scenario/STAR processing. A polling connection failure delays
that cycle's jobs. Previously the outer catch reported every failure and waited
five seconds. Queues and privacy requests are durable; their actual incident-time
backlog and user impact were not available for verification.

Reviewed: `PrivacyService`, `PracticeJobProcessor`, `ScenarioStarJobProcessor`,
`RetentionWorker`, `RealtimeNotificationBroadcaster` and Npgsql registration.
There is no added EF global execution strategy or transaction replay. Existing
claims, stale-work recovery and provider side-effect boundaries remain in place.

## Corrective implementation

- Classify transient Npgsql transport failures separately from permanent SQLSTATE
  constraint/programming errors; walk wrapped exception chains.
- Practice polling and realtime DB polling use equal-jitter exponential backoff:
  initial wait at least 5 seconds, capped at 30–60 seconds; successful cycles reset
  the outage state. Delays honor cancellation and each cycle opens a fresh scope.
- Practice outage warning/Sentry report occurs first, then at most every five
  minutes for the continuous outage. Permanent errors still use the error/reporter
  path, not the benign connectivity path. Existing Sentry sanitizer remains active.
- Retention already polls independently at five-minute intervals with a durable
  sweep schedule, bounded batches and failure suspension; no rapid retry added.
- No retry of an entire transaction containing a paid/provider side effect.
  New durable provider attempts prevent reauthorizing a previously reserved attempt,
  including an ambiguous timeout or failed downstream persistence.

## Validation and limits

Tests cover increasing/capped jitter, report suppression/reset, transient/permanent
classification and the actual Worker recovering after a controlled failure then
stopping promptly. A PostgreSQL integration regression injects a connection-open
failure **before privacy claim**, reconnects using a new scope, completes the real
purge and verifies a second poll neither deletes twice nor repeats quota voids.
It does not claim to reproduce the unidentified production network failure or every
possible mid-transaction failure. Existing PostgreSQL job-race tests cover competing
workers and deletion/provider races. See PR/CI for execution totals; PostgreSQL tests
require a disposable `NEXORA_POSTGRES_TEST_CONNECTION`, never production.

## Operator follow-up (separate approval)

1. Retrieve the precise Sentry event/socket code and Neon metrics at 13:04:19 UTC;
   correlate connection exhaustion, compute events and network errors without PII.
2. Alert on DB readiness failures, oldest pending-job age, repeated five-minute outage
   reports and durable budget rejections. Investigate rather than auto-refund/replay.
3. Review migration and proxy configuration prerequisites in
   [the abuse audit](../security/ai-speech-abuse-audit.md) before rollout.
4. Keep rollback additive: retain the new ledger table and its attempt history when
   reverting application code. Old binaries do not enforce new provider budgets.

No production config/schema changes, paid provider calls, restarts, Render changes,
DNS changes, deployment or merge were performed for this hotfix.
