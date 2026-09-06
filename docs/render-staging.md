# Nexora Internal Staging Deployment (Render Free)

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
│   └── Shared Ephemeral Filesystem (/tmp/nexora-storage)     │
└─────────────────────────────────────────────────────────────┘
       │                              │                 │
       ▼                              ▼                 ▼
Neon PostgreSQL (Non-Prod)    Google Gemini API   SePay Sandbox
(ep-crimson-art-...-singapore)  (gemini-2.5-flash) (pay-sandbox.sepay.vn)
```

### Why API and Worker are Co-located in One Container
- Current Nexora uses `LocalStorageProvider` for candidate CV/document uploads.
- When an applicant uploads a resume, `Nexora.Api` stores the file under `Storage__Local__RootPath` (`/tmp/nexora-storage`).
- The background outbox processor `Nexora.Worker` later inspects the database queue, retrieves the file from that local path, and performs text extraction and profiling.
- On Render Free, separate services do **not** share a local filesystem, and persistent disks are unavailable.
- By packaging both executables inside the same Docker container managed by `scripts/render-entrypoint.sh`, both processes read and write to the same `/tmp/nexora-storage` directory while the container is running.

> [!WARNING]
> **DO NOT COPY THIS SINGLE-SERVICE FILESYSTEM TOPOLOGY TO PRODUCTION.**  
> Production requires adopting a durable shared object storage provider (e.g. S3-compatible cloud storage) before splitting `Nexora.Api` and `Nexora.Worker` into independently scalable services.

---

## Render Free Tier Limitations (Accepted for Staging)

The frontend team and stakeholders accept the following constraints of Render Free:
1. **Ephemeral Raw Storage**: Files uploaded to `/tmp/nexora-storage` are ephemeral. Any restart, redeploy, or container spin-down will clear raw uploads. (Neon PostgreSQL data remains fully persistent).
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
| **Local Storage Path** | `/tmp/nexora-storage` |
| **EF Core Migrations** | Pre-run bundle `/app/nexora-migrate` via `scripts/render-entrypoint.sh` |

---

## Environment & Secrets Configuration

### Non-Secret Environment Variables
- `ASPNETCORE_ENVIRONMENT=Staging`
- `DOTNET_ENVIRONMENT=Staging`
- `Features__Ai=true`
- `Features__Payment=true`
- `Features__Upload=true`
- `Storage__Local__RootPath=/tmp/nexora-storage`
- `Authentication__Jwt__Issuer=Nexora.Api`
- `Authentication__Jwt__Audience=Nexora.Frontend`
- `Authentication__RefreshCookie__SameSite=None`
- `Authentication__RefreshCookie__Secure=true`
- `Billing__Payment__Provider=sepay`
- `Billing__Sepay__Environment=Sandbox`
- `Frontend__AllowedOrigins__0=http://localhost:3000`
- `Frontend__AllowedOrigins__1=http://localhost:5173`
- `Frontend__AllowedOrigins__2=http://127.0.0.1:3000`
- `Frontend__AllowedOrigins__3=http://127.0.0.1:5173`
- `Frontend__AllowedOrigins__4=https://nexora-backend-q32b.onrender.com`

### Secure Secrets (Configure in Render Dashboard or CLI)
- `ConnectionStrings__Postgres`: Non-production Neon connection string (`sslmode=require`).
- `Authentication__Jwt__SigningKey`: 64+ character cryptographically secure key.
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
