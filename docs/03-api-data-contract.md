# API và mô hình dữ liệu

**Status:** Approved implementation baseline  
**Last updated:** 2026-08-21

## Quy ước API

- Base URL: `/api/v1`.
- Xác thực: ASP.NET Core Identity; access token ngắn hạn gửi bằng `Authorization: Bearer` và refresh token rotation qua cookie `HttpOnly`, `Secure`, `SameSite` phù hợp theo ADR-003.
- Response lỗi: `{ "error": { "code": "...", "message": "...", "requestId": "..." } }`.
- `Idempotency-Key` bắt buộc khi duplicated execution không an toàn: checkout/order creation, chargeable resume-analysis/job creation, interview start, official-answer submission, interview completion/report trigger, relevant admin mutation và externally triggered processing khi chưa có provider event ID mạnh hơn. Không yêu cầu cho GET/read-only. Cùng actor + operation + key + equivalent payload trả kết quả gốc; reuse key với payload khác trả `409 IDEMPOTENCY_CONFLICT`.

## API MVP

| Method | Endpoint | Mục đích |
| --- | --- | --- |
| GET | `/me` | Profile và entitlement hiện hành. |
| GET | `/plans` | Gói, giá, quyền lợi từ server. |
| POST | `/checkout-sessions` | Tạo order/URL thanh toán. |
| POST | `/webhooks/payments/:provider` | Nhận webhook đã verify chữ ký. |
| POST | `/uploads/presign` | Cấp signed URL upload CV/avatar. |
| POST | `/resumes` | Ghi metadata file sau upload. |
| POST | `/resume-analyses` | Tạo job phân tích CV–JD. |
| GET | `/resume-analyses/:id` | Trạng thái/kết quả phân tích. |
| POST | `/interviews` | Tạo và bắt đầu phiên phỏng vấn. |
| GET | `/interviews/:id` | Đọc session state/question hiện tại của owner. |
| POST | `/interviews/:id/answers` | Lưu câu trả lời, tạo câu hỏi/feedback tiếp theo. |
| POST | `/interviews/:id/complete` | Kết thúc, tạo report. |
| GET | `/interviews/:id/report` | Đọc report immutable của owner khi completed. |
| GET | `/dashboard` | Tiến độ, lịch sử và quota. |

### Admin API — tối thiểu cho vận hành

Tất cả route dưới đây yêu cầu policy `Admin`, reason code đối với mutation, `Idempotency-Key` và audit log. Admin không mặc định được đọc CV/transcript nội dung.

| Method | Endpoint | Mục đích |
| --- | --- | --- |
| GET | `/admin/orders` | Tìm order theo ID, user/email đã redacted, trạng thái payment. |
| POST | `/admin/orders/:id/refunds` | Yêu cầu/refund qua provider theo DEC-02; revoke entitlement theo BR-07. |
| POST | `/admin/users/:id/entitlement-adjustments` | Cấp/thu quota hoặc access với reason, expiry và ticket reference. |
| GET | `/admin/audit-logs` | Xem audit metadata theo actor/action/resource/time. |
| GET | `/admin/operations/jobs` | Xem trạng thái job lỗi để retry có kiểm soát. |

## Request and response contracts quan trọng

### Tạo interview

```json
POST /api/v1/interviews
{
  "role": "Business Analyst",
  "seniority": "junior",
  "interviewType": "behavioral",
  "difficulty": "medium",
  "resumeId": "01J...",
  "jobDescriptionId": "01J..."
}

201 Created
{
  "data": { "id": "01J...", "status": "starting", "firstQuestion": null },
  "meta": { "usage": { "action": "reserve", "used": 0, "limit": 3 } }
}
```

`resumeId` và `jobDescriptionId` là optional nhưng, nếu có, phải thuộc user hiện tại. Server tự tính entitlement; client không gửi `plan`, `score`, `userId`, price hay quota. Client poll `GET /interviews/{id}` cho tới `active` + first question hoặc terminal failure. Reservation được consume theo server-observable transaction dưới đây, không theo network receipt.

Canonical StartInterview transactions:

```text
API initial:
  BEGIN
  validate auth + optional CV/JD ownership + entitlement + Idempotency-Key
  reserve quota + create session starting + create outbox/job
  COMMIT

Worker success:
  BEGIN
  persist first usable question + reserve -> consume + starting -> active
  COMMIT

Worker terminal failure before activation:
  BEGIN
  reserve -> void + starting -> failed
  COMMIT
```

Worker retries are idempotent and must not duplicate sessions, questions, usage events or job effects. Consumption is based on persisted server state and successful activation, not proof of browser receipt. Once `active`, disconnect, refresh, navigation away, no answer or later AI/report failure does not automatically void usage; report retry remains idempotent and free of another interview charge.

### Trả lời interview

```json
POST /api/v1/interviews/{interviewId}/answers
Idempotency-Key: 4e8b...
{
  "questionId": "01J...",
  "content": "Câu trả lời dạng text của ứng viên",
  "durationSeconds": 94
}
```

Response có answer đã lưu và question tiếp theo hoặc `isComplete: true`. Một question chỉ nhận một answer chính thức trừ khi endpoint revision được định nghĩa riêng.

### Error envelope

```json
{
  "error": {
    "code": "QUOTA_EXCEEDED",
    "message": "Bạn đã dùng hết lượt phỏng vấn của gói hiện tại.",
    "requestId": "01J..."
  }
}
```

| HTTP | Code | Client action |
| --- | --- | --- |
| 400 | `VALIDATION_ERROR` | Hiển thị lỗi field, không retry. |
| 401 | `UNAUTHENTICATED` | Mở login, giữ `next` URL an toàn. |
| 403 | `FORBIDDEN` / `QUOTA_EXCEEDED` | Không retry; đề nghị quyền/gói phù hợp. |
| 404 | `NOT_FOUND` | Không tiết lộ tài nguyên của user khác. |
| 409 | `IDEMPOTENCY_CONFLICT` / invalid state | Refresh trạng thái, không gửi lặp mù quáng. |
| 429 | `RATE_LIMITED` | Retry theo `Retry-After`. |
| 5xx | `INTERNAL_ERROR` | Hiển thị lỗi tổng quát với requestId. |

## Dữ liệu lõi

| Nhóm | Bảng chính |
| --- | --- |
| Identity | ASP.NET Core Identity tables, `user_profiles` |
| Billing | `plans`, `plan_prices`, `orders`, `payment_events`, `subscriptions`, `entitlements`, `usage_events` |
| CV | `resumes`, `resume_files`, `job_descriptions`, `resume_analyses` |
| Interview | `interview_sessions`, `interview_questions`, `interview_answers`, `interview_reports` |
| Practice | `scenario_attempts`, `star_drafts`, `star_feedback` |
| Operations | `idempotency_keys`, `outbox_events`, `audit_logs` |

### Cột bắt buộc và constraints

- Tất cả record user-owned có `id`, `user_id`, `created_at`, `updated_at`; `id` là UUID/ULID.
- `orders` có unique `(payment_provider, provider_transaction_id)` khi provider ID đã tồn tại.
- `payment_events` unique `(provider, provider_event_id)`.
- `usage_events` immutable, có `action` thuộc `reserve|consume|void|adjustment`, `quantity`, `source_id`, `idempotency_key` unique.
- `interview_answers` unique `(interview_session_id, question_id)` cho answer chính thức.
- `resume_files.storage_key` không phải public URL; URL download được sinh sau authorization.

## State machines

```text
Order: pending -> paid -> fulfilled
       pending -> expired | failed
       paid | fulfilled -> refunded (theo DEC-02/BR-07)
Resume: uploaded -> processing -> ready | failed | deleted
Analysis: queued -> processing -> completed | failed | cancelled
Interview: canonical tại 08-data-model.md
  draft -> starting -> active -> completing -> completed
           starting -> failed
           active -> abandoned
```

Chỉ `active` nhận official answer. Completion xảy ra đúng một lần; report generation idempotent; optimistic concurrency/versioning chống transition/answer trùng. Terminal interview states không đổi trừ administrative/recovery process explicit và audited.

## Phân quyền

- Người dùng chỉ truy vấn record có `record.user_id = authenticatedUser.id`.
- Admin actions dùng role riêng và audit log; không dùng email hard-code ở frontend.
- Download file luôn qua signed URL thời hạn ngắn sau khi xác minh owner.
- Payment webhook không dùng bearer user token; chỉ tin payload sau verify signature và event ID chưa từng xử lý.
- Response dùng DTO allow-list thay vì trả trực tiếp EF entity để tránh lộ field nội bộ/role/secret.
