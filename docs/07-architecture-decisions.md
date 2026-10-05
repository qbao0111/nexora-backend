# Architecture Decision Records (ADR) — Nexora

**Status:** Approved implementation baseline; production adapter selections approved in PR #126, remaining operational/legal gates distinguished below
**Last updated:** 2026-10-05

## ADR-001 — Modular monolith Three-Layer

**Decision:** một solution .NET 10 LTS, deploy một API và một worker; source chia module feature nhưng chạy theo Presentation → Business → Data/Integration.

**Rationale:** phù hợp vài chục–vài trăm user, một team và nghiệp vụ có transaction chung (user, quota, AI job, payment). Không dùng microservice hoặc distributed event bus ở MVP.

**Structure:**

```text
Nexora.Api            controllers, auth middleware, DTO mapping
Nexora.Business       services, policies, validation, interfaces
Nexora.Data           EF Core DbContext, repositories, migrations
Nexora.Integrations   AI/payment/storage/email adapters
Nexora.Worker         bounded background jobs only
```

`Domain` có thể là folder/namespace trong `Business` ở MVP. Không tách `Application`, `Domain`, `Infrastructure` thành bốn project chỉ để theo mẫu Clean Architecture; chỉ tách khi complexity/test boundary thực sự cần.

## ADR-002 — Identity ownership

**Decision:** dùng ASP.NET Core Identity với `ApplicationUser : IdentityUser<Guid>` và EF Core PostgreSQL store.

**Consequences:** không tự tạo bảng `users` chứa `password_hash`, `provider` hoặc JWT logic song song với Identity. Thêm profile fields qua `ApplicationUser`/`UserProfile`; Identity migrations là nguồn sự thật cho credential, role, token/revocation.

## ADR-003 — Auth transport

**Decision:** access token ngắn hạn trong memory và gửi bằng `Authorization: Bearer`; refresh token rotation qua cookie `HttpOnly`, `Secure`, `SameSite` phù hợp domain. API/web domains cụ thể thuộc DEC-04 và được chốt trước production deploy.

**Consequences:** áp dụng HTTPS, CORS allow-list, CSRF protection cho endpoint dùng cookie. Không lưu refresh token trong `localStorage`.

## ADR-004 — Storage and document processing

**Decision:** PostgreSQL chỉ lưu metadata và structured data. CV/avatar/audio production dùng private object storage qua signed URL ngắn hạn. `IStorageProvider` chọn `LocalStorageProvider` cho development/testing hoặc `R2StorageProvider` cho private Cloudflare R2 objects; local filesystem không phù hợp production. A2 bổ sung durable `upload_intents`, signed PUT và finalize/actual-object validation; account/hosting enablement vẫn chịu DEC-04.

**Consequences:** DB chỉ giữ `storage_key`, checksum, MIME, size, state; không lưu public `file_url`. File upload qua scan/validate pipeline rồi worker extract PDF/DOCX.

## ADR-005 — Billing and quota ledger

**Decision:** tách `plan`, `plan_price`, `order`, `payment_event`, `subscription`, `entitlement`, `usage_event`; không dùng một bảng subscription `UNIQUE(user_id)` và counter mutable làm nguồn sự thật.

**Consequences:** hỗ trợ lịch sử mua nhiều gói, 14/90 ngày, refund, adjustment và audit. Usage action canonical là `reserve`, `consume`, `void`, `adjustment`; quota reserve/consume/void trong transaction với idempotency key, không `COUNT()` rồi increment ở service vì race condition. Ledger immutable là audit source of truth; correction chỉ thêm adjustment.

**Canonical Phase-2 transaction:** mở transaction `ReadCommitted`, lock entitlement projection đang active bằng PostgreSQL `SELECT ... FOR UPDATE`, kiểm tra expiry/limit và idempotency key, ghi `usage_event` reservation + session `starting` + outbox event, cập nhật projection rồi commit. Khi worker có first usable question đã validate, một transaction persist question + chuyển reservation thành `consume` + chuyển session `starting → active`. Terminal failure trước activation dùng một transaction chuyển reservation thành `void` + session thành `failed`. Retry không tạo duplicate effect. Nếu chọn `Serializable` thay thế, phải có bounded retry khi serialization failure; không được chỉ dựa vào application memory lock.

## ADR-006 — AI is asynchronous and bounded

**Decision:** document extraction, CV analysis, report generation chạy worker/job; tạo câu hỏi tiếp theo có thể synchronous chỉ khi timeout budget cho phép.

**Consequences:** mọi job có state, correlation ID, retry/backoff, dead-letter evidence, prompt/model version, token/cost. MVP dùng một evaluation có structured output và rule validation; không gọi "strict + lenient + final" ba lần cho mỗi answer.

## ADR-007 — JSONB boundary

**Decision:** JSONB chỉ dùng cho provider payload snapshot, rubric/evidence linh hoạt và model output versioned. Field được filter/sort/report thường xuyên là cột chuẩn hoá.

**Consequences:** `overall_score`, session status, timestamps, plan dates, user IDs, usage action phải là cột; không nhét toàn bộ domain vào JSONB.

## Production enablement decisions DEC-01 through DEC-04

Các DEC này **không block backend/local development, Phases 0–3 hoặc integration tests dùng internal Gemini/DeepSeek development adapters và test-project doubles**. Chúng chỉ block capability production tương ứng khi cần choice production-specific.

| Decision | Status | Còn cần chốt | Chỉ block |
| --- | --- | --- | --- |
| DEC-01 | Adapter resolved: official DeepSeek; not a claim that all cost controls are resolved | Operator-approved environment-controlled model; global monetary budget, automatic spend alerts/circuit-break thresholds and provider balance monitoring still require operational approval | Production traffic requires the cost/abuse operational checklist below |
| DEC-02 | Adapter resolved: payOS; checkout/query/verified webhook implemented | Refund/invoice/tax policy remains a separate business/legal concern; no automated refund or invoice/tax workflow is claimed | Affected financial/legal rollout, not adapter selection/startup |
| DEC-03 | Deferred — required before production enablement | Final CV/JD/transcript/recording/log retention periods; approved Terms/Privacy/AI/recording text | Affected production data processing/go-live |
| DEC-04 | Deployment path prepared: VPS + Neon + R2 + Resend; not deployed by this PR | Operator accounts/secrets, DNS/TLS, backups, mail verification and rollout approval remain required | Actual deployment/cutover |

Temporary implementations are approved for engineering: configuration-driven `GeminiAiProvider` (default) and optional official `DeepSeekAiProvider` for local text-AI evaluation, `FakePaymentProvider`, and `LocalStorageProvider` for development/testing. `R2StorageProvider` plus the durable `R2UploadProvider` are the available private production-storage/upload adapters; the final R2 account/hosting enablement remains under DEC-04. Document extraction is local only; DeepSeek selection requires no Gemini credentials. A deterministic AI test double may exist only inside the test project. These implementations do not create the remaining production vendor/account decisions.

### PR #126 production approval and control audit

The product owner's corrective explicitly approves **official DeepSeek + payOS +
private R2**. This supersedes the historical blanket AI/payment startup prohibition,
not the remaining go-live obligations. Production startup permits only R2 storage,
DeepSeek when AI is enabled, and payOS when payment is enabled. Gemini and SePay
sandbox remain local/staging choices; fake payment remains development/testing.
Speech uses its existing Azure validators/feature flag; Resend validation remains.
Disabled AI/payment do not require their approved adapter's credentials at startup;
unknown selectors still fail ordinary configuration validation. No provider secrets
are included in guard errors. Production payOS callbacks must be public HTTPS;
local HTTP loopback remains available only outside Production.

DEC-01 implementation evidence:

- Owner-authenticated controllers; interview entitlement reservation/consume/void
  transactions and scenario/STAR feature quotas remain authoritative.
- `RateLimits:AiJob` defaults to 10 requests/user/hour; answers have their own
  bounded policy. Limits are per API process, **not** distributed spend accounting.
  Production operators must keep `RateLimits:Disabled=false`.
- `StructuredAiExecutor` caps provider calls at 2 per purpose execution; the
  DeepSeek adapter has `MaxAttempts=1`. Existing job/recovery budgets are separate
  bounded executions, so this is not a two-call cap over a whole interview.
- Official HTTPS endpoint, required key/model (max 80 characters), timeout 1..120s,
  retry-delay bounds and per-purpose reasoning validation are unchanged. Models
  and reasoning remain environment-controlled; no model/retry/token changes here.
- Safe token/latency/purpose/model/correlation telemetry exists. It is **not** a
  persisted monetary ledger, global budget, automatic spend alert or balance guard.
  No global/provider monetary usage guard was found in the current implementation.
- `Features__Ai=false` plus container recreation blocks API AI mutations and pauses
  queued AI processing (including scenario/STAR) while privacy processing continues.
  Paused jobs/reservations are not failed/refunded; re-enable to resume them. This is
  an immediate operational breaker after restart, not an automatic threshold breaker.

Before real traffic, the operator must approve model/cost exposure, monitor provider
usage/balance and alerts, designate a responder and rehearse the kill switch. This
PR does not invent a monetary limit or claim a missing global spend control exists.

DEC-02 evidence: payOS creates hosted checkout, validates responses/order references,
queries provider state and verifies webhook signatures before idempotent fulfillment.
Server-owned amount/currency and order ownership checks are retained. The SDK has
`MaxRetries=0`; no retry/security/fulfillment changes. The immutable usage ledger's
refund/revocation rules (BR-07) are not an implemented refund API. No automated refund,
invoice or tax issuance is claimed; those policies require separate owner/legal
approval where applicable. `Features__Payment=false` blocks new checkout after
recreation, **not** callback/query reconciliation for existing orders. Keep valid
payOS credentials for outstanding orders even during an emergency checkout pause.

DEC-04 is a prepared Linux amd64 VPS/GHCR/SSH/Caddy deployment path using Neon/R2/
Resend and the approved domain, not evidence of accounts, DNS or deployment actions.
See [VPS production runbook](VPS_PRODUCTION_DEPLOYMENT.md) for the operator gates.
