# Frontend Local Integration — Nexora

**Status:** Approved internal-development handoff
**Last updated:** 2026-08-25

This guide is the browser-integration handoff for a new frontend. Contract ownership remains in [03-api-data-contract.md](03-api-data-contract.md), formal requirements remain in [SRS.md](SRS.md), and backend release gates remain in [05-test-strategy.md](05-test-strategy.md).

## 1. Recommended frontend baseline

- React, Vite and TypeScript.
- React Router for routing and TanStack Query for server state/polling.
- Zod (or equivalent) at the API boundary where runtime validation adds value.
- Tailwind CSS may be used for the design system.
- Environment variable: `VITE_API_ORIGIN=http://localhost:5088`.

Do not copy the old localStorage mock authentication, quota, interview, report or dashboard state into the new implementation. The backend is authoritative. Do not call Gemini, payment or storage providers from the browser.

## 2. Start the backend

From the backend repository root:

```powershell
pwsh ./scripts/run-development.ps1
```

This builds, applies Neon development migrations, then keeps API and Worker running together. It reads the shared connection and Gemini settings from local .NET user-secrets without printing them.

| Resource | Development URL |
| --- | --- |
| API origin | `http://localhost:5088` |
| API base | `http://localhost:5088/api/v1` |
| OpenAPI | `http://localhost:5088/openapi/v1.json` |
| Vite frontend | `http://localhost:5173` |

Use `localhost` consistently for browser cookie testing. The development CORS allow-list also accepts `http://127.0.0.1:5173`, but mixing the two host names can change cookie/site behavior.

## 3. API client invariants

- Successful JSON is `{ "data": ... }`; unwrap only at the client boundary.
- Errors are `{ "error": { "code", "message", "requestId" } }`; preserve `code` and `requestId` for UI/support.
- Send `Authorization: Bearer <accessToken>` for protected calls.
- Keep the short-lived access token in memory, not localStorage. The refresh token is an HttpOnly cookie.
- Use `credentials: "include"` for auth calls and preferably for the shared fetch client.
- On one `401`, call `POST /api/v1/auth/refresh` once through a shared refresh lock, replace the in-memory access token and retry the original request once. Never create an infinite refresh loop.
- A mutation requiring `Idempotency-Key` gets one UUID per user intent. Reuse it for transport retries of that same intent; create a new UUID for a genuinely new action.
- Do not add idempotency keys to GET requests.
- Treat `404` while polling a report as “not available yet” only when the interview is known to be `completing`; otherwise surface the error.

## 4. Core journey contract

| Step | Request | Important behavior |
| --- | --- | --- |
| Register | `POST /api/v1/auth/register` | Returns access token/user and sets refresh cookie. |
| Login | `POST /api/v1/auth/login` | Same session behavior as register. |
| Restore session | `POST /api/v1/auth/refresh` | Cookie-based; use `credentials: "include"`. |
| Current user/quota | `GET /api/v1/me` | Server-owned identity, billing and entitlement state. |
| Plans | `GET /api/v1/plans` | Public; price and quota come from server. |
| Checkout | `POST /api/v1/checkout-sessions` | Body `{ planPriceId }`; requires idempotency key. |
| Upload intent | `POST /api/v1/uploads/presign` | Body `{ fileName, contentType, size }`. |
| Upload bytes | `PUT {uploadUrl}` | Raw PDF/DOCX bytes with the declared content type; `uploadUrl` is relative to API origin. |
| Finalize CV | `POST /api/v1/resumes` | Body `{ uploadToken }`; returns resume initially `uploaded`. |
| Create JD | `POST /api/v1/job-descriptions` | Body `{ title, content }`. |
| Start analysis | `POST /api/v1/resume-analyses` | Body `{ resumeId, jobDescriptionId }`; idempotency key; CV must first become `ready`. |
| Poll analysis | `GET /api/v1/resume-analyses/{id}` | Poll `queued/processing` to `completed/failed`. |
| Start interview | `POST /api/v1/interviews` | Body role/seniority/type/difficulty and optional CV/JD IDs; idempotency key. |
| Poll interview | `GET /api/v1/interviews/{id}` | Poll `starting` to `active/failed`; only `active` accepts answers. |
| Official answer | `POST /api/v1/interviews/{id}/answers` | Body `{ questionId, content, durationSeconds }`; idempotency key. Render `nextQuestion` or `isComplete`. |
| Complete | `POST /api/v1/interviews/{id}/complete` | Empty body; idempotency key; returns `202` and `completing`. |
| Report | `GET /api/v1/interviews/{id}/report` | Poll until report exists; render server score, rubric/evidence and disclaimer. |
| Dashboard | `GET /api/v1/dashboard` | Database-backed interview/report history and quota summary. |

Poll with a bounded interval (start near 1 second, back off to 3–5 seconds), stop on terminal state, cancel when leaving the screen and show an explicit retry action after timeout.

## 5. Development payment fulfillment

`FakePaymentProvider` creates a pending order but the browser must never know the webhook secret. During local FE development, copy the returned `orderId` and run:

```powershell
pwsh ./scripts/complete-fake-payment.ps1 -OrderId "ORDER_ID"
```

Then refetch `GET /api/v1/me`; the paid entitlement is fulfilled through the same authenticated, idempotent fake webhook path used by integration tests. This helper is development-only. It is not a production checkout UX and does not resolve DEC-02.

## 6. Suggested frontend delivery slices

1. App shell, routes, error boundary and API client.
2. Register/login/session restore/logout with protected routes.
3. Plans, current entitlement and dashboard.
4. CV upload, JD creation and analysis polling.
5. Interview state machine, official answers and refresh persistence.
6. Report rubric/evidence/action plan and history navigation.
7. Browser E2E for the complete journey, cross-user negatives and responsive/accessibility review.

Each slice should remove its corresponding mock state only after the real server path passes. STAR/scenario pages remain Should-priority and must not block the Must-priority journey above.
