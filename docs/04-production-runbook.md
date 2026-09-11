# Production runbook

**Status:** Approved operational baseline; vendor/legal enablement gates remain deferred  
**Last updated:** 2026-09-11

Nexora runs on .NET 10 LTS, ASP.NET Core 10 Web API and EF Core 10 with a
PostgreSQL database, a modular-monolith API/Business/Data boundary and a
background Worker. DEC-01 through DEC-04 do not block development or
integration testing; they must be resolved before the affected real production
capability is enabled.

## Environment matrix

| Environment | PostgreSQL | Object storage | Email | AI | Payment | Sentry | Frontend origin / transport |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Development | Dedicated Neon `development` branch (or local test DB) | `local` | `noop` | Development Gemini/DeepSeek (Fake only in the test harness) | Fake | Optional/disabled | Explicit localhost HTTP origins; secure cookies off |
| Testing | Test SQLite/Postgres supplied by the harness | `local` | Test double/`noop` | Fake provider | Fake | Optional/disabled | Test origins; no external calls |
| Staging | Dedicated non-production Neon | `r2` private bucket | Resend | Approved development/staging adapter | SePay Sandbox | DSN + release required | At least one operator-supplied HTTPS frontend origin; secure cross-site cookies |
| Production | Approved managed PostgreSQL | `r2` private bucket | Approved Resend setup | DEC-01-approved provider/budget | DEC-02-approved provider | DSN + release required | Verified HTTPS frontend origin(s); secure cookies |

Staging and Production fail closed when PostgreSQL, R2, Resend, JWT, Sentry,
release or CORS configuration is incomplete. Development and Testing keep
their local/fake adapters where safe. The API and Worker use the same deployed
environment and Sentry release.

## Configuration and secrets

Use environment variables or an approved secret manager. The canonical names
are listed in [`.env.example`](../.env.example). Never commit `.env` files,
credentials, connection strings, DSNs, signed URLs or real candidate data.

Required deployed values include:

```text
ConnectionStrings__Postgres
Authentication__Jwt__SigningKey
Authentication__EmailVerification__PublicUrl
Storage__Provider=r2
Storage__R2__AccountId
Storage__R2__Bucket
Storage__R2__AccessKeyId
Storage__R2__SecretAccessKey
Storage__R2__Endpoint (absolute HTTPS)
Email__Provider=resend
Email__FromAddress
Email__FromName
Email__Resend__ApiKey
Email__Resend__ApiBaseUrl=https://api.resend.com
Sentry__Dsn
Sentry__Release, or Render's RENDER_GIT_COMMIT fallback
Frontend__AllowedOrigins__0 (and any additional verified origins)
```

Production `Storage:Provider` is always `r2`, even when uploads are disabled;
filesystem storage is never a production fallback. R2 objects are private and
are accessed through storage abstractions and short-lived signed URLs where
supported. The browser upload/finalize contract is documented in
[`03-api-data-contract.md`](03-api-data-contract.md); no public bucket is
required.

### CORS and cookies

Production and Staging require non-empty, HTTPS-only frontend origins. The
startup validator rejects wildcard, loopback (including localhost), userinfo,
query strings, fragments, paths other than `/`, control/whitespace tricks and
duplicate ambiguity. The Render backend URL is not a frontend origin. Enter
the actual Vercel/custom frontend origin in the deployment dashboard; do not
guess a domain. SignalR uses the same allowlist and credential requirements.

Access tokens remain in frontend memory and use the `Authorization` header.
Refresh tokens are `HttpOnly`, `Secure` and `SameSite=None` for cross-site
staging/production integration. Do not put tokens in localStorage.
Cookie mutation requests carrying an `Origin` are checked against the same
allowlist; requests without an `Origin` remain supported for non-browser
clients, while browser integrations must send their verified frontend origin.

## Release and deployment

The Render entrypoint requires `ASPNETCORE_ENVIRONMENT` and
`DOTNET_ENVIRONMENT` to match (or defaults both to `Staging`), then runs the EF
migration bundle once and starts API and Worker with the same inherited
environment. It creates a local storage
directory only when `Storage__Provider=local`; R2 deployments do not depend on
the container filesystem. If either child exits unexpectedly, the sibling is
stopped and the container exits non-zero.

Render provides `RENDER_GIT_COMMIT`. An explicit `Sentry__Release` wins;
otherwise `scripts/render-entrypoint.sh` exports the commit as
`Sentry__Release` before launching both processes. If neither exists in a
deployed environment, startup fails. Do not use unsupported Blueprint
interpolation such as `Sentry__Release=$RENDER_GIT_COMMIT`.

For Railway, use the same semantic variable names and set
`Sentry__Release` explicitly unless an operator has verified a current,
platform-supported commit variable. Nexora is not claiming a Railway deploy
in this repository.

## Release pipeline

1. Pull the reviewed commit and run the repository CI-equivalent restore,
   Release build, unit/integration tests, EF drift check, vulnerability audit,
   changed-file format/analyzer checks and `git diff --check`.
2. Back up the target database and rehearse the migration on an isolated
   staging target.
3. Configure secret and non-secret deployment values, deploy, and check
   `/health/live`, `/api/v1/health`, login, private upload/finalize, extraction,
   and a synthetic interview job.
4. Observe safe logs, queue/operations health and Sentry for the agreed window.
5. Roll back the application/configuration first. Use a forward database fix or
   a rehearsed migration rollback; never silently switch a deployed R2 setup to
   local storage.

## Security and privacy minimums

- ASP.NET Core Identity, owner authorization and BOLA negative tests remain
  authoritative for every user-owned resource.
- Validate detected MIME/signature, size and extension. Malware scanning is an
  optional defence, never a guarantee.
- Keep secrets out of logs. Structured logs contain request/correlation IDs,
  resource IDs, outcome, duration, status and exception type only; never raw
  CV/JD/answers/transcripts, prompts, provider responses, headers, tokens,
  connection strings or signed URLs.
- Preserve deletion/export capability, immutable usage history, webhook
  signature verification and idempotency. Obtain explicit consent before any
  future audio/video recording.
- Sentry keeps `SendDefaultPii=false`, excludes request bodies/query strings,
  auth/cookie data and candidate content, and uses safe `request_id`, `service`,
  `environment` and `release` metadata. Do not mark external alert delivery
  complete until it has been verified in the approved Sentry project.

## Health, alerts and recovery

Use `/api/v1/health/operations` for queue lag, payment pending and recent job /
deletion failure state. For an incident, start with the request/correlation ID
and timestamp, then check Sentry, Render/Railway service state, PostgreSQL and
R2 provider status without enabling raw-payload logging.

Configure API 5xx, Worker failure, queue lag, webhook verification and payment
pending alerts according to traffic after the relevant vendor decision. This
repository provides safe signals and health checks; it does not claim a live
alert destination has been configured.

Run `scripts/verify-postgres-backup.ps1 -ConfirmIsolatedTarget` only against a
disposable isolated database. Never run a restore drill against production.

## Rate limits and staging performance baseline

Keep rate-limit values in configuration rather than hard-coding them. The
initial production baseline is login 5 attempts/15 minutes/IP and 10/15
minutes/email; refresh 30/hour/session; upload 10/hour/user; checkout
5/hour/user; AI job start 10/hour/user outside plan quota; and answer submit
20/5 minutes/session. Return `429` with `Retry-After`, then recalibrate after
load testing through a reviewed configuration change.

For the initial MVP staging rehearsal (dozens to hundreds of users), target
50 virtual users for 10 minutes at a steady 15 RPS for CRUD/API traffic, plus
a 100-user/60-second burst capped at 30 RPS. Keep non-AI synchronous endpoint
P95 below 500 ms and error rate below 1%; job creation should enqueue rather
than wait for a model. This is a technical baseline, not a capacity promise.

## Sentry alert verification

Create the approved backend Sentry project and configure separate `service:api`
and `service:worker` alerts for unhandled errors/HTTP 5xx and worker failure
spikes. Choose thresholds from observed staging traffic. Trigger a controlled
staging error and verify `service`, environment, release and safe
`request_id` metadata while confirming that request bodies, query strings,
headers, cookies, candidate content and secrets are absent. Only mark alert
delivery complete after an operator verifies receipt; if Sentry is unavailable,
use structured logs, health endpoints and provider status without enabling raw
payload logging.

## Backup and restore rehearsal

For T-10 run
`scripts/verify-postgres-backup.ps1 -ConfirmIsolatedTarget` with
`NEXORA_BACKUP_SOURCE` and `NEXORA_RESTORE_TARGET` supplied out of band. The
target must be disposable and named to indicate `isolated`, `restore`,
`drill` or `test`. Set `NEXORA_RESTORE_API_READ_URL` and a synthetic account's
`NEXORA_RESTORE_API_TOKEN` only for the rehearsal. Never place connection
strings or tokens in command history, logs or source control, and never drill
against the production target.

## Production enablement gates

- **DEC-01:** approve the production AI provider/model and budgets before real
  production AI traffic. Development/test adapters remain usable locally.
- **DEC-02:** approve the Vietnamese payment provider, refunds, invoices and
  tax handling before real payments. SePay Sandbox/FakePayment tests remain
  available before that decision.
- **DEC-03:** approve retention periods and legal/privacy text before affected
  production processing.
- **DEC-04:** approve hosting, storage, domains, mail and infrastructure
  accounts before production deployment.

A13 hardens configuration and does not resolve or bypass these gates.

## Go-live checklist

- [ ] Verified production domain, DNS, HTTPS and OAuth redirects.
- [ ] Dedicated PostgreSQL, private R2, Resend and Sentry values configured.
- [ ] JWT key meets the deployed minimum and is not a development/default key.
- [ ] Frontend allowlist contains only verified HTTPS origins.
- [ ] Backup/restore rehearsal and deletion/export checks completed.
- [ ] Payment webhook signature/idempotency tested in the approved sandbox.
- [ ] Sentry privacy and alert delivery externally verified.
- [ ] Legal retention/privacy and consent text approved.

See [`render-staging.md`](render-staging.md) for the Render Blueprint and
Railway-equivalent configuration guide.

## Incident response

| Severity | Example | Initial action |
| --- | --- | --- |
| P1 | Secret exposure, unauthorized data access, widespread payment error | Disable the affected integration, rotate secrets, preserve evidence, notify the owner and stop deployment. |
| P2 | API unavailable or major AI/payment backlog | Roll back the application/configuration or disable the affected feature, then inspect queue and database health. |
| P3 | One failed job/report | Record request/job ID, retry according to policy and open an issue if it repeats. |

After a P1/P2 incident capture a timeline, impact, root cause, corrective
action and owner/due date.
