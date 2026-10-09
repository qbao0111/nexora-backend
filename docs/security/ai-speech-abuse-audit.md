# AI / Speech abuse and financial-safety audit

Status: proposed code changes, **not production remediation**. Date: 2026-10-09.
Audited main: `9ba2939e6a732409a599dd745be9a626f7702f3c`.
Related [incident evidence](../incidents/2026-10-08-worker-npgsql.md).

## Assessment

**Confirmed code weaknesses:** authentication partitions read raw X-Forwarded-For;
forwarded-header trust lists were emptied; rotating refresh cookies created unrelated
refresh partitions; HTTP rate limits alone did not bound durable paid execution across
restarts/replicas; Speech issuance checked ownership but not interview state/age.
These are source findings, not demonstrated exploitation or proof of excess billing.

**FIXED IN CODE:** explicit proxy trust, validated RemoteIpAddress partitions, bounded
forwarded headers, outer refresh-IP protection, global burst/concurrency, bounded JSON
bodies and SignalR ingress; durable provider and enqueue admission; Speech session
state/age and issuance budgets. Normal throttling returns structured 429, not 500.

**NOT VERIFIED:** incident root cause; actual Sentry/Neon metrics; actual Cloudflare
account plan/rules; mobile Speech implementation; actual provider invoice/unit prices.
No approved Cloudflare read connector was available. No monetary saving is claimed.

## Current HTTP policies and gaps

All following are defaults, per API process, fixed windows with queue length zero.
Read-only container env inspection found no rate-limit overrides; no live load test
was performed. Counters reset with API restarts and multiply across replicas.

| Policy | Default / partition | Routes |
| --- | --- | --- |
| Authentication | 5 / 15 min / validated IP | register, verify-email, resend-verification, login, mobile/login |
| LoginEmail | 10 / 15 min / normalized email | login paths; Identity lockout also applies |
| PasswordRecovery | 5 / 15 min / IP | forgot-password, reset-password (existing email controls retained) |
| Refresh | 30 / hour / hashed browser cookie, otherwise IP | browser/mobile refresh |
| RefreshIp (new outer layer) | 120 / hour / IP | both refresh paths, independent of supplied cookie |
| Upload | 10 / hour / user | upload presign and avatar upload |
| Checkout | 5 / hour / user | create/refresh checkout |
| AiJob | 10 / hour / user | analyses, interview create/practice-again/continue/retries/complete, scenario submit, STAR create |
| Answer | 20 / 5 min / user + interview | official answer |
| SpeechToken | 10 / 15 min / user | interview Speech token |
| Realtime (new) | 30 / minute / user | authenticated `/hubs/realtime` negotiate/connect requests |
| ContentReport | 5 / hour / user | content reports |
| External deletion | request 5 / hour; confirm 10 / 15 min / IP | anonymous external deletion |
| Burst (new) | 300 / minute / IP | all routes including callbacks/health |
| ConcurrentRequests (new) | 100 active requests / process | all routes; no waiting queue |

Other routes (history/read models, admin/site mutations, JD/resume metadata, scenario
draft/retry, feedback, logout, payment webhooks) have global protection but not a new
endpoint-specific policy. Existing ownership, pagination/input validation, entitlement,
signature and idempotency checks remain authoritative. This is not a blanket reduction
of endpoint limits. History page validation and multipart upload limits are unchanged.

JSON is capped at 1 MiB before model binding (Kestrel cap also covers unknown-length
JSON). Existing narrower field/multipart limits still apply. SignalR receive size is
32 KiB, parallel invocations per client is one and expired auth closes connections.
Pre-auth concurrency has three independent process-local pools (no queued waiters):

| Configuration | Default | Scope / lifetime |
| --- | --- | --- |
| `RateLimits:ConcurrentRequests` | 100 | Regular short HTTP, including SignalR POST negotiate/send and DELETE |
| `RateLimits:Realtime:ConcurrentRequests` | 100 | All WebSocket upgrades and GET `/hubs/realtime` transports (WebSocket, SSE, long polling) |
| `RateLimits:Realtime:ConcurrentPerIp` | 20 | Same realtime transports per validated connection IP; NAT shares this cap |
| `RateLimits:Health:ConcurrentRequests` | 10 | GET liveness/readiness/operations health only |
| `RateLimits:Health:ConcurrentPerIp` | 2 | Health requests per validated IP |
| `RateLimits:Health:BurstPermitLimit` | 60/minute/IP | Independent bounded health burst bucket, not an exemption |

Ordinary and realtime traffic still share the existing 300/minute/IP pre-auth burst
bucket; refresh keeps its outer IP window. Long-lived transport permits are held until
the request ends, including cancellation/disconnect/failure; they never consume regular
HTTP or health permits. Short SignalR control requests use HTTP permits so a full
transport pool cannot prevent send/disconnect. Authenticated SignalR user-partitioned
30/minute policy, owner delivery, JWT/security-stamp checks and auth-expiry closure are
unchanged. Global realtime and per-IP caps bound active WebSockets across users/IPs;
long polling shares transport occupancy, not a new durable count of idle logical sessions.
NAT fairness is a tradeoff: 20 active realtime transports per public IP. No unchecked
forwarded header or client-selected transport parameter bypasses admission.

At default limits total admitted occupancy is bounded at 100 HTTP + 100 realtime +
10 health, rather than one shared 100-slot pool. Regular HTTP remains 100, not increased.
Health cannot be starved by either pool or their IP burst bucket, but its own bounded
pool/burst can reject health floods and dependent DB/network failures can still make
readiness fail. This does not guarantee availability under host/DB/edge exhaustion.
All new limits must be positive (startup validation); disabled limits remain forbidden
in Production. Defaults require no production configuration mutation in this corrective.

Middleware: bounded forwarding → framework trusted forwarding → correlation/telemetry
→ exception/JSON bounds → CORS → pre-auth IP burst/refresh/concurrency gate →
authentication → user/endpoint rate limiter → authorization → feature gate.
Rejected pre-auth requests do not reach JWT security-stamp DB lookups. This is a
process-local bound, not distributed edge protection. Shared NATs share anonymous/burst limits, but authenticated
paid operations retain independent user partitions. Monitor false positives.

## Endpoint → paid execution matrix

Every row requires authenticated owner access, feature gate and existing product
entitlements. Idempotency-Key replays existing logical jobs; new keys cannot bypass
the durable per-user/global budgets. New sessions still consume normal product quota;
report retries remain free in **product quota**, not free provider spending.

| Trigger / job | Purpose and maximum output tokens per call | Jobs / amplification and existing safeguards |
| --- | --- | --- |
| POST `/api/v1/resume-analyses` | `resume.profile` 3000 if uncached; `resume.analysis` 4096, truncation repair 8192 | One analysis job; cached profile avoids repeat profiling; owner resume/JD + analysis entitlement + AiJob |
| POST `/api/v1/interviews`, `/{id}/practice-again` | optional uncached `resume.profile` 3000; `interview.first-question` 500 per prepared question | One start job; first usable question consumes reservation; preactivation failure voids; no active-session refund |
| POST `/api/v1/interviews/{id}/continue`, `/questions/retry` | `interview.first-question` 500 per missing prepared question | One question-plan job per accepted logical operation; state/CAS/idempotency prevent duplicate activation; Free continuation unchanged |
| POST `/api/v1/interviews/{id}/answers` | `interview.evaluate` 6000, truncation repair 8192; optional `interview.followup` 500 | One evaluation job per answer; active-only answer, unique logical answer, Answer policy; may enqueue final report |
| POST `/api/v1/interviews/{id}/complete`, `/report/retry`, `/results/retry` | `interview.report` 6000; result retry may requeue failed answer evaluation | State/idempotency controls; report is immutable once completed; report failure stays completing; no extra product quota for retry |
| POST `/api/v1/scenario-attempts/{id}/submit` | `scenario.evaluate` 4000 | One job; owner + scenario entitlement + AiJob; drafts/retry creation alone do not call AI |
| POST `/api/v1/star-attempts` | `star.evaluate` 6000 | One job when submission accepted; owner + STAR entitlement + AiJob |

**Every logical purpose invocation: maximum two provider calls**, initial + at most one
repair/recovery. Multi-question preparation is multiple bounded logical invocations,
not a claim of two calls for the entire interview. DeepSeek adapter MaxAttempts remains
one; no HTTP retry handler introduced. Transport timeout remains at most 60 seconds.
No scoring, prompt/model/reasoning or output-token budget was changed. Estimates are
bounded call/token units, **not currency**; no pricing was invented. Invoice costing
needs the actual administrator/provider prices and cache/reasoning billing metadata.

## Durable spending admission (separate from subscription quota)

`Ai:Budget` defaults:

| Key | Default |
| --- | --- |
| UserHourlyCalls / UserDailyCalls / GlobalDailyCalls | 60 / 200 / 2000 |
| UserHourlyTokens / UserDailyTokens / GlobalDailyTokens | 500000 / 1000000 / 50000000 |
| GlobalInFlight | 4 |
| MaximumInputBytes | 256000 (combined reservation bound includes output + 1024 overhead) |
| MaximumQueuedJobs | 500 pending/processing AI outbox jobs |
| PurposeHourlyCalls | optional per-purpose user/hour overrides; fallback UserHourlyCalls |
| CooldownFailureThreshold / CooldownSeconds | 3 transport/429 failures in a minute / 30 seconds |
| MaximumSpeechSessions | 2 distinct issued interview IDs / rolling 10 minutes |

Daily windows are UTC calendar days; hour is rolling. Defaults are configurable
operational ceilings, not a purchased plan or guaranteed normal-workload capacity.
Review actual traffic before approving rollout.

PostgreSQL transaction advisory lock `782346109` serializes admission across API/Worker
instances. Reserve persists **before** a paid call. Unique job/purpose/operation/attempt
rejects an already admitted attempt; operation identity derives from answer/question
metadata, not caller-chosen correlation text. Reserved units conservatively count UTF-8
input/instructions/schema bytes + max output tokens + 1024. Actual DeepSeek prompt and
completion usage is recorded where available, but never reduces the reserved charge.
Missing usage remains unknown. Gemini does not currently reconcile actual usage.

Timeout, invalid output, cancellation, ambiguous transport and downstream save failure
never refund provider budget. A three-minute lease releases **concurrency only** after
crash, not charge or replay rights. An attempt without a successful checkpoint fails closed rather than
guessing whether a provider charged. This can require an operator recovery decision;
it must not silently refund or replay an ambiguous call. Completion is idempotent.
Global cooldown rejects new AI admissions; the configured default does not suppress
the established two-call repair after a single failure. Failures under concurrency
may already have admitted up to GlobalInFlight calls before cooldown is observed.

Queue lock `782346110` serializes paid enqueue transactions. Depth check and outbox
insert commit/rollback together; saturated enqueue returns `429 AI_QUEUE_FULL` with
Retry-After. Existing backlog is preserved, not dropped; queue cap is not a per-user
fairness scheduler. Final-answer auto-enqueue saturation detaches only the uncommitted
report job, preserving the committed ready evaluation and processed answer job.
The completing session plus all-ready answers is a durable scheduling intent. Each
Worker poll reconciles up to 20 such sessions in deterministic ID order, locks each
session, and enqueues once capacity returns. Existing report jobs (including failed)
and completed reports exclude initial reconciliation. Explicit report retries retain
their capacity-error/idempotency contract; failed reports are not automatically retried.
Execution budget denial is observable job failure under the
existing retry contract, not an unlimited new automatic retry loop.

Admission DB unavailability fails closed before new provider calls. Durable rows
contain operational IDs/counters/failure class plus an optional **private typed AI
response checkpoint**, not the provider HTTP envelope, credentials or input prompt.
The checkpoint can contain candidate-derived personal data: no public/list/export DTO
or log exposes it. Account deletion clears checkpoint/fingerprint in its transaction
while retaining charged counters (prevents budget reset); the new ledger has no
automatic retention sweep in this hotfix. A separately approved operational retention
policy must preserve budget/replay audit needs; do not manually clear current windows.

### Successful-provider-result reconciliation (PR129 corrective)

Migration `20261009093710_AddProviderResultCheckpoints` adds three nullable columns only:
`ResultJson`, `ResultFingerprint`, and `FailureRetryHint`.
Successful typed responses and actual usage are atomically checkpointed with completion
before domain persistence. Identity remains job/purpose/operation/attempt; a SHA256
fingerprint binds input, instructions, schema/model/version, budget and reasoning (not
correlation). Reclaim uses the checkpoint, re-runs semantic validation, and never makes
a duplicate provider call. Recorded failure kind/hint reconstructs the same second-
attempt request after restart without repeating the failed first paid call. Changed
context fails closed; max two paid attempts remains.

Completion uses fresh scopes/transactions, five-second storage-attempt timeouts and
cancellation-aware equal-jitter 5–60s backoff for transient PostgreSQL outages. It holds
the successful response in memory until DB recovery or worker shutdown; this is explicit
**storage-only reconciliation**, not an adapter/provider retry. Repeated completion after
an ambiguous DB commit is idempotent. Owner-before-ledger lock ordering matches privacy
purge; late completion cannot recreate content for a deletion-requested account.
Practice job connectivity failures remain reclaimable instead of marking the answer
failed; normal ten-minute stale-claim recovery uses a new Worker scope.

Residual crash window: death after a paid provider response but **before its first durable
checkpoint** can still lose that in-memory response. With PostgreSQL unavailable no
durable success can be promised; the pre-call reservation prevents regeneration/double
charge, remains charged, and requires explicit recovery. No exactly-once external
provider guarantee or unapproved file-based PII journal is claimed. Checkpoint-bearing
rows require normal DB access/backup protections and future retention review.

Telemetry: existing adapter/executor logs record purpose/model/request correlation,
attempt/duration/token usage/failure; admission records job/user internal reference,
reserved/actual units, lease/cooldown and denial reason. Estimated currency is unknown,
not zero. No raw payload/token/credentials are logged. Queue depth can be obtained
from operational pending/processing counts; no unauthenticated metrics endpoint added.

## Azure Speech: bounded issuance is NOT metered upstream use

POST `/api/v1/speech/interviews/{id}/token` retains auth/owner check, no-store/private
response, SpeechToken rate limit, singleton cache and concurrent refresh semaphore.
STS cache refresh defaults to 8 minutes, returned usable lifetime 9 minutes; Azure
tokens remain reusable upstream for approximately 10 minutes.

New: only starting/active owned interviews, at most `Speech:MaximumSessionMinutes`
(default120, valid15–480) since creation. No new paid-only entitlement: normal session
creation/continuation product quota still governs access. Each issuance consumes one
durable call unit (even cache hits), one conservative token unit and temporarily shares
the global admission slot. AI and Speech issuance **share** the above ledger budgets;
per-purpose overrides allow stricter `speech.token` ceilings. At most two distinct
interview IDs receive issuance in ten minutes; this is not two actual Azure streams.
Inactive/expired sessions return409; issuance-budget exhaustion returns429 Retry-After.

Inspected web frontend: `speechTokenManager.ts` caches per interview, deduplicates
in-flight token requests and invalidates on cleanup; `azureSpeechPlayback.ts` calls
Azure Speech SDK TTS directly. `useSpeechRecognition.ts` uses browser Web Speech API,
not Azure STT. Mobile implementation was not available for verification. Reconnect
clients must respect expiry and429; no frontend source changed.

An issued Azure bearer token can be reused outside Nexora until it expires. Backend
cannot reliably meter/revoke every direct synthesis or recognize operation. No claim
of a hard Azure spending/session cap. Azure TTS character usage and STT audio usage
must be observed on the actual Azure resource. A server-metered gateway/shorter scoped
transport is a **separate design proposal**, not implemented here.

**REQUIRES AZURE DASHBOARD CONFIG:** isolated production/dev resources, Azure Monitor
consumption/error alerts, Cost Management actual/forecast budget alerts (e.g. operator
chosen 50/80/100% thresholds), review service quotas/support options. Alerts alone
are not a guaranteed spending cap. See [Speech quotas](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-services-quotas-and-limits)
and [cost alerts](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/cost-mgt-alerts-monitor-usage-spending).

## Proxy / edge rollout prerequisites — separate approval

**REQUIRES PRODUCTION CONFIG:** add the *observed* Caddy-to-container peer to
`ReverseProxy:KnownProxies:0` (observed172.18.0.1 on Oct9; reverify before rollout).
`ReverseProxy:ForwardLimit=1` expects Caddy to supply exactly the validated origin.
Without this, safe rejection of untrusted headers groups clients by proxy IP, which
can cause shared rate limits. Do not deploy the code without reviewing this behavior.

Caddy must trust only official Cloudflare proxy CIDRs, strict right-to-left traversal,
accept CF-Connecting-IP only from those peers and overwrite upstream X-Forwarded-For
with its validated client identity. Direct-origin requests must use the peer address,
not attacker headers. Preserve API/WebSocket routing and unrelated Caddy sites.
Actual Caddy trust configuration was not freshly verified in this audit; **do not
apply a guessed configuration**. Framework default loopback trust remains; no all-IP
trust, raw header parsing or ForwardedHost trust is added. Production refuses
`RateLimits:Disabled=true` and automatic unrestricted forwarded-header mode.
References: [ASP.NET proxy guidance](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0),
[Caddy reverse proxy](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy).

Exact operator prerequisites (documentation only; no configuration changed):

1. Observe the socket peer **inside the deployed container**, not merely the Compose
   gateway address. Set `ReverseProxy__KnownProxies__0=<observed peer IP>` and
   `ReverseProxy__ForwardLimit=1`; re-check after Docker network recreation. Do not
   add all private networks, Cloudflare CIDRs or end-user IPs to the application's
   KnownProxies: the application trusts its direct Caddy/Docker peer only.
2. In the existing Caddy server-options block, `trusted_proxies static` must contain
   the current [official Cloudflare IPv4/IPv6 ranges](https://www.cloudflare.com/ips/)
   only; enable `trusted_proxies_strict` and use `client_ip_headers CF-Connecting-IP`.
   These options require verifying the installed Caddy version and merging with
   existing options/sites, never replacing the active configuration wholesale.
3. In the Nexora site's existing `reverse_proxy 127.0.0.1:10000`, explicitly overwrite
   `header_up X-Forwarded-For {client_ip}` and `header_up X-Forwarded-Proto {scheme}`.
   Do not copy the incoming XFF/CF header verbatim. `{client_ip}` must resolve through
   the above trusted-peer policy; direct non-Cloudflare traffic uses the socket peer.
   See [Caddy server proxy options](https://caddyserver.com/docs/caddyfile/options#trusted-proxies).
4. Validate the merged configuration before any separately approved activation;
   verify two real clients produce distinct app RemoteIpAddress values, forged XFF/CF
   headers cannot change those values, HTTPS/CORS/SignalR still work, and port10000
   remains loopback-only. Keep unrestricted automatic forwarded-header mode OFF.

Fail-safe when missing/mismatched: the application ignores untrusted forwarding and
limits the actual peer (all clients may share the Caddy/Docker IP); it does **not**
disable limiting or trust raw headers. Missing KnownProxies is not a startup error,
so rollout must STOP until the above two-client/spoof checks pass. Malformed explicit
IP/invalid ForwardLimit fails options validation. Trusting the local peer before
Caddy sanitization is verified is unsafe and is not an approved rollout sequence.

**REQUIRES CLOUDFLARE APPROVAL:** actual plan must be checked first. Conditional
lowest-plan proposal: one path-only rate rule for `/api/v1/auth/login`,
`/api/v1/auth/mobile/login`, `/api/v1/auth/register`, `/api/v1/auth/forgot-password`
and `/api/v1/auth/resend-verification`: per-IP30 requests/10 seconds, Block10 seconds
**only if the account exposes those durations/actions**. Expression:

```text
http.request.uri.path in {"/api/v1/auth/login" "/api/v1/auth/mobile/login" "/api/v1/auth/register" "/api/v1/auth/forgot-password" "/api/v1/auth/resend-verification"}
```

Path-only matching can affect other hosts in the zone using those paths; review zone
scope. Host/method/custom counting fields and additional rules require verified plan
capabilities; no paid feature is assumed. Never browser-challenge mobile API calls,
PayOS/server callbacks or authenticated realtime. Observe legitimate NAT traffic
before enforcement. A separately approved origin firewall policy could restrict80/443
to Cloudflare while retaining operator SSH; no firewall/DNS/proxy changes here.
See [Cloudflare rate-rule capabilities](https://developers.cloudflare.com/waf/rate-limiting-rules/).

## Testing, rollout and emergency procedures

**TESTED:** deterministic fake providers only; Worker recovery/cancellation/backoff,
proxy spoof/IPv6/malformed bounds, canonical429/Retry-After, body413, Production
disabled-limiter rejection, budget concurrency/duplicates/restart/cooldown, queue
saturation, Speech owner/state/age/budget, existing Azure cache/single-refresh tests
and maximum-two-call/no-nested-retry suites. Real PostgreSQL admission/recovery and
existing race tests run where the disposable PostgreSQL dependency exists; PR/CI
records distinguish skips from passes.

Migration `20261009084857_AddProviderCallReservations` is additive: one new table,
three indexes, nullable usage/completion/failure fields; no existing data rewrite,
FK cascade or provider calls. Apply once through approved release migration bundle.
Advisory locks are short admission/enqueue transactions, never held across AI calls;
watch lock waits and DB capacity. Keep the ledger on rollback; old code loses guardrails.

Emergency changes need operator authorization: set `Features__Ai=false` and recreate
the approved stack through its runbook to block new AI endpoints/pause Worker AI jobs
(privacy remains enabled); `Features__Speech=false` blocks new issuance but existing
Azure tokens remain usable until expiry. Real Azure emergency containment may require
resource/key controls, with service interruption and separate approval. Neither flag
was changed. No code in this PR claims to have blocked production traffic already.
