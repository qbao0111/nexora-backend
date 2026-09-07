# Architecture Decision Records (ADR) — Nexora

**Status:** Approved implementation baseline; DEC-01–04 deferred for production enablement  
**Last updated:** 2026-08-21

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

**Decision:** PostgreSQL chỉ lưu metadata và structured data. CV/avatar/audio production dùng private object storage qua signed URL ngắn hạn. `IStorageProvider` cho phép `LocalStorageProvider`/development adapter trước khi DEC-04 chọn production vendor; local filesystem không phù hợp production.

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
| DEC-01 | Deferred — required before production enablement | Production AI provider/model; per-user/global budgets, alert/circuit-break limits | Real production AI traffic |
| DEC-02 | Deferred — required before production enablement | Vietnamese payment provider; refund, invoice and tax handling | Real production payment/refund flow |
| DEC-03 | Deferred — required before production enablement | Final CV/JD/transcript/recording/log retention periods; approved Terms/Privacy/AI/recording text | Affected production data processing/go-live |
| DEC-04 | Deferred — required before production enablement | Production API/worker/database/object-storage hosting, domains, mail provider and infrastructure accounts | Affected production deployment |

Temporary implementations are approved for engineering: configuration-driven `GeminiAiProvider` (default) and optional official `DeepSeekAiProvider` for local text-AI evaluation, `FakePaymentProvider`, and `LocalStorageProvider`/development storage. `GeminiDocumentOcrProvider` remains the document fallback for either text provider. A deterministic AI test double may exist only inside the test project. These implementations do not create a production vendor decision.
