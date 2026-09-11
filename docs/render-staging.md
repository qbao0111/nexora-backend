# Render staging deployment

**Status:** Approved deployment configuration baseline; operator values remain required  
**Last updated:** 2026-09-11

This guide describes the current Render Free staging shape for Nexora. It is a
deployment guide, not evidence that a live service, Sentry project, domain or
Railway environment has been configured.

## Topology and lifecycle

The existing Docker image runs `Nexora.Worker` and `Nexora.Api` in one Render
web service. They remain co-located for the current small staging workload and
to keep the migration/startup/shutdown lifecycle simple; they do **not** share
product files through a local filesystem. Resume objects are private R2
objects, and both processes receive the same environment and Sentry release.

Startup runs the migration bundle once, then starts API and Worker. If either
child exits unexpectedly, `scripts/render-entrypoint.sh` stops the sibling and
returns a failure. `/health/live` is the liveness check and
`/api/v1/health` is the readiness check.

## Render Blueprint

`render.yaml` contains names and non-secret defaults only. Values marked
`sync: false` must be entered in the Render dashboard or an approved secret
manager; never put credentials in the Blueprint.

Non-secret staging settings include:

```text
ASPNETCORE_ENVIRONMENT=Staging
DOTNET_ENVIRONMENT=Staging
Features__Ai=true
Features__Payment=true
Features__Upload=true
Ai__Provider=gemini
Storage__Provider=r2
Authentication__Jwt__Issuer=Nexora.Api
Authentication__Jwt__Audience=Nexora.Frontend
Authentication__RefreshCookie__SameSite=None
Authentication__RefreshCookie__Secure=true
Billing__Payment__Provider=sepay
Billing__Sepay__Environment=Sandbox
Email__Provider=resend
Email__FromName=Nexora
Email__Resend__ApiBaseUrl=https://api.resend.com
```

The operator must supply these values through secure Render configuration:

```text
ConnectionStrings__Postgres
Authentication__Jwt__SigningKey
Authentication__EmailVerification__PublicUrl
Email__FromAddress
Email__Resend__ApiKey
Storage__R2__AccountId
Storage__R2__Bucket
Storage__R2__AccessKeyId
Storage__R2__SecretAccessKey
Storage__R2__Endpoint
Sentry__Dsn
Sentry__Release (optional when RENDER_GIT_COMMIT is available)
Frontend__AllowedOrigins__0
Ai__Gemini__ApiKey
Ai__Gemini__Model
Billing__Sepay__MerchantId
Billing__Sepay__SecretKey
```

R2 values describe a private bucket and an HTTPS S3-compatible endpoint. No
public-read ACL or public bucket URL is required. `Frontend__AllowedOrigins`
must be the actual HTTPS Vercel/custom frontend origin; do not enter the
Render backend URL and do not guess a domain. Add more indexed values only for
additional verified frontend origins.

Render supplies `RENDER_GIT_COMMIT`. The entrypoint preserves an explicit
`Sentry__Release`; when it is empty, it exports `RENDER_GIT_COMMIT` as the
release before launching API and Worker. If neither is available, deployed
startup fails closed. The release is safe metadata, not a secret.

## Operational checklist

1. Create separate staging Neon, R2, Resend, SePay Sandbox and Sentry values.
2. Enter the variables above in the Render dashboard. Keep R2 objects private.
3. Confirm the frontend origin and public email-verification URL are HTTPS.
4. Deploy the Blueprint and verify `/health/live`, `/api/v1/health` and the
   OpenAPI route from the actual service host.
5. Confirm migration bundle completion and inspect safe API/Worker logs. Logs
   may contain request/resource IDs and outcomes, never credentials, tokens,
   signed URLs, connection strings or CV/transcript contents.
6. Exercise a synthetic login, private upload/finalize, extraction and worker
   path. Use request/correlation IDs for diagnosis.
7. Configure and verify Sentry alerts separately. A11 sanitization remains
   enabled; do not mark live alert delivery complete without external evidence.

Render Free can sleep or cold-start. That is an operational limitation, not a
reason to restore local filesystem storage. R2 and Neon are the durable
staging stores.

## Railway equivalent (future option)

Nexora is not currently declared deployed to Railway. If Railway is selected
under DEC-04, configure the same semantic keys using Railway environment
variables:

```text
ConnectionStrings__Postgres
Storage__Provider=r2
Storage__R2__AccountId
Storage__R2__Bucket
Storage__R2__AccessKeyId
Storage__R2__SecretAccessKey
Storage__R2__Endpoint
Sentry__Dsn
Sentry__Release
Frontend__AllowedOrigins__0
Email__Provider=resend
Email__Resend__ApiKey
Authentication__Jwt__SigningKey
Authentication__EmailVerification__PublicUrl
```

Use a Railway-supported commit/release variable only after the operator has
verified its current name and behavior. Otherwise set `Sentry__Release`
explicitly for both API and Worker. Do not invent a variable name or rely on
Blueprint interpolation. Production AI, payment, legal/privacy and hosting
decisions remain gated by DEC-01 through DEC-04.

## Rollback

Rollback the application image/configuration first. Do not silently fall back
from R2 to local storage in a deployed environment. If R2 is unavailable,
keep the deployment failed/observable and restore the approved R2 settings or
perform a forward fix. Database migrations remain the source-controlled
bundle and require the normal backup/restore rehearsal.

See [04-production-runbook.md](04-production-runbook.md) for the environment
matrix, production gates, incident response and Sentry privacy controls.
