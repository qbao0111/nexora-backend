# Nexora Internal Staging Deployment (Render Free)

> **Storage correction (2026-09-25):** The historical local `/tmp/nexora-storage` topology below is not durable. A redeploy/restart can retain database metadata while deleting CVs, avatars, and site assets. Staging and Production now reject `Storage:Provider=local` at startup unless `Storage:Local:PersistentVolumeConfigured=true` is explicitly set for a genuinely mounted persistent volume. Render Free `/tmp` is **not** such a volume. Configure private R2 before deploying this hotfix; never set the persistent-volume flag for `/tmp`.

> Required Render environment keys (set secrets in Render, never in Git): `Storage__Provider=r2`, `Storage__R2__AccountId`, `Storage__R2__Bucket`, `Storage__R2__AccessKeyId`, `Storage__R2__SecretAccessKey`, and `Storage__R2__Endpoint`. Confirm all keys are present without printing values. The endpoint must satisfy the application's R2 HTTPS validation. Existing objects lost from the old `/tmp` container cannot be reconstructed: affected avatars/site images return 404 and must be uploaded again; missing CV objects require a new upload and must not have their DB metadata silently deleted.

## Overview

This document details the internal staging topology, architecture rationale, configuration, limitations, and operational runbook for hosting the **Nexora Backend** on **Render Free** for frontend team integration.

---

## Staging Architecture & Topology

```text
Vercel Frontend (https://nexora-staging.vercel.app or local http://localhost:5173)
       │
       ▼ (HTTPS / Cross-Site Cookie / Bearer JWT)
Render Free Web Service (`nexora-staging`)
┌─────────────────────────────────────────────────────────────┐
│ Docker Container (Linux x64, ASP.NET Core 10 Runtime)       │
│                                                             │
│   ├── Nexora.Api (Web API listening on 0.0.0.0:$PORT)       │
│   │                                                         │
│   ├── Nexora.Worker (Outbox queue polling & background jobs)│
│   │                                                         │
│   └── Private R2 object storage via existing adapter       │
└─────────────────────────────────────────────────────────────┘
       │                              │                 │
       ▼                              ▼                 ▼
Neon PostgreSQL (Non-Prod)    Google Gemini API   SePay Sandbox / R2
(ep-crimson-art-...-singapore)  (gemini-2.5-flash) (pay-sandbox.sepay.vn)
```

### API and Worker storage
- API and Worker currently run in one Render container via `scripts/render-entrypoint.sh`, but share private R2 objects through the existing storage adapter.
- The former `/tmp/nexora-storage` approach was ephemeral and caused persisted metadata to point at missing objects after restart/deploy.
- Render Free does not provide a persistent local disk for this topology. Co-location does not make `/tmp` durable.

> [!WARNING]
> **Do not use `/tmp` for deployed uploads.** R2 credentials and bucket must be configured before this hotfix is deployed.

---

## Render Free Tier Limitations

The remaining Render Free constraints are:
1. **Ephemeral local filesystem**: `/tmp` is not used for durable user assets. Previously lost objects cannot be recovered from database metadata.
2. **Idle Spin-Down**: The Web Service automatically spins down after 15 minutes of inactivity.
3. **Cold Starts**: The first request after spin-down experiences a cold start delay (typically 30–50 seconds).
4. **Worker Pauses with Service**: Background processing in `Nexora.Worker` pauses when the container is spun down due to inactivity.
5. **Constrained Resources**: Limited CPU and 512 MB RAM.

---

## Service Specifications

| Property | Value |
|---|---|
| **Service Name** | `nexora-staging` |
| **Service Type** | Web Service |
| **Runtime** | Docker (Multi-stage .NET 10) |
| **Plan** | Free |
| **Region** | Oregon (`oregon`) |
| **Auto-Deploy** | Disabled during initial branch validation; enabled on `main` post-merge |
| **Health Check Route** | `/health/live` (Readiness: `/api/v1/health`) |
| **Durable storage** | Private R2 bucket, configured by deployment environment |
| **EF Core Migrations** | Pre-run bundle `/app/nexora-migrate` via `scripts/render-entrypoint.sh` |

### Runtime source of truth

On 2026-09-11, the Render dashboard verified that `nexora-staging` was connected to `main` and live on commit `e25d4022955ad4a097632926ab725044c829cde0`. The dashboard configuration is authoritative for the running service; the branch value in `render.yaml` is only repository reference/configuration and does not prove the deployed commit. The R2 values below are required target configuration, not a claim that they are already present in Render.

---

## Required Environment & Secrets Configuration

### Non-Secret Environment Variables
- `ASPNETCORE_ENVIRONMENT=Staging`
- `DOTNET_ENVIRONMENT=Staging`
- `Features__Ai=true`
- `Features__Payment=true`
- `Features__Upload=true`
- `Storage__Provider=r2`
- `Authentication__Jwt__Issuer=Nexora.Api`
- `Authentication__Jwt__Audience=Nexora.Frontend`
- `Authentication__RefreshCookie__SameSite=None`
- `Authentication__RefreshCookie__Secure=true`
- `Billing__Payment__Provider=sepay`
- `Billing__Sepay__Environment=Sandbox`
- `Email__Provider=resend`
- `Email__FromName=Nexora`
- `Frontend__AllowedOrigins__0=http://localhost:3000`
- `Frontend__AllowedOrigins__1=http://localhost:5173`
- `Frontend__AllowedOrigins__2=http://127.0.0.1:3000`
- `Frontend__AllowedOrigins__3=http://127.0.0.1:5173`
- `Frontend__AllowedOrigins__4=https://nexora-backend-q32b.onrender.com`

### Secure Secrets (Configure in Render Dashboard or CLI)
- `Storage__R2__AccountId`, `Storage__R2__Bucket`, `Storage__R2__AccessKeyId`, `Storage__R2__SecretAccessKey`, `Storage__R2__Endpoint`: private R2 connection settings; configure all before deploying.
- `ConnectionStrings__Postgres`: Non-production Neon connection string (`sslmode=require`).
- `Authentication__Jwt__SigningKey`: 64+ character cryptographically secure key.
- `Authentication__EmailVerification__PublicUrl`: Frontend URL (e.g. `https://nexora-staging.vercel.app` or custom HTTPS domain).
- `Email__FromAddress`: Verified sending email address (e.g. `onboarding@resend.dev` or domain address).
- `Email__Resend__ApiKey`: Resend API key (`re_...`).
- `Ai__Gemini__ApiKey`: Google Gemini API key.
- `Ai__Gemini__Model`: `gemini-3.5-flash-lite`.
- `Billing__Sepay__MerchantId`: SePay Sandbox Merchant ID.
- `Billing__Sepay__SecretKey`: SePay Sandbox Secret Key.

---

## Frontend Integration Handoff

- **Staging API Base URL**: `https://nexora-backend-q32b.onrender.com/api/v1`
- **Swagger UI (Interactive API Explorer)**: `https://nexora-backend-q32b.onrender.com/swagger`
- **OpenAPI v1 Document**: `https://nexora-backend-q32b.onrender.com/openapi/v1.json`
- **Authentication**:
  - Access Token: Stored in frontend memory only. Sent via `Authorization: Bearer <token>`.
  - Refresh Token: Handled automatically via `HttpOnly`, `Secure`, `SameSite=None` cookie.
  - Required Request Header: `credentials: "include"` on all `fetch`/`axios` requests.
- **SePay IPN Webhook URL**: `https://nexora-backend-q32b.onrender.com/api/v1/webhooks/payments/sepay`
