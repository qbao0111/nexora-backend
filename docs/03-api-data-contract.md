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
| GET | `/me/export` | Export allowlisted core profile/billing/practice data của owner; không trả storage key, credential hoặc provider secret. |
| POST | `/me/deletion-requests` | Yêu cầu xoá bất đồng bộ; bắt buộc `Idempotency-Key`, revoke session ngay và trả `202`. |
| GET | `/plans` | Gói, giá, quyền lợi từ server. |
| POST | `/checkout-sessions` | Tạo order/URL thanh toán. |
| GET | `/checkout-sessions/:id` | Đọc trạng thái checkout của owner. |
| POST | `/checkout-sessions/:id/refresh` | Reconcile checkout pending từ provider sandbox khi IPN chậm. |
| POST | `/webhooks/payments/fake` | Nhận webhook fake đã ký cho test deterministic nội bộ. |
| GET | `/webhooks/payments/vnpay` | Nhận VNPAY Sandbox PAY 2.1.0 IPN dạng query-string, trả JSON theo protocol VNPAY. |
| POST | `/uploads/presign` | Cấp signed URL upload CV/avatar. |
| POST | `/resumes` | Ghi metadata file sau upload. |
| GET | `/resumes/:id` | Đọc trạng thái xử lý CV và lỗi an toàn của owner. |
| POST | `/resume-analyses` | Tạo job phân tích CV–JD. |
| GET | `/resume-analyses/:id` | Trạng thái/kết quả phân tích. |
| POST | `/interviews` | Tạo và bắt đầu phiên phỏng vấn. |
| GET | `/interviews/:id` | Đọc session state/question hiện tại của owner. |
| POST | `/interviews/:id/answers` | Lưu câu trả lời, tạo câu hỏi/feedback tiếp theo. |
| POST | `/interviews/:id/complete` | Kết thúc, tạo report. |
| GET | `/interviews/:id/report` | Đọc report immutable của owner khi completed. |
| GET | `/dashboard` | Tiến độ, lịch sử và quota. |
| GET | `/health/operations` | Vendor-neutral aggregate operational state (`Healthy`/`Degraded`), không trả count hay resource ID mặc định. |

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

### Export và xoá dữ liệu cá nhân

`GET /api/v1/me/export` chỉ trả core data thuộc owner. `POST /api/v1/me/deletion-requests` tạo audit state `queued → processing → completed|failed`; cùng user và `Idempotency-Key` trả request gốc. Sau khi accepted, access/refresh session hiện tại không còn hợp lệ. Worker xoá private object và personal practice records rồi anonymize Identity account; billing/usage ledger được giữ làm audit theo retention được phê duyệt. Thời hạn retention production vẫn do DEC-03 quyết định.

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

### Upload và xử lý CV

Sau khi `POST /uploads/presign`, client PUT đúng bytes file vào `uploadUrl`, rồi gọi `POST /resumes` với `uploadToken`. Response resume ban đầu có `status: "uploaded"`; client poll `GET /api/v1/resumes/{id}` cho tới `ready` hoặc `failed`. Worker dùng `extracting` cho local PdfPig/OpenXML, `ocr_fallback` khi quality gate yêu cầu document fallback Gemini, rồi `ready` khi đã lưu canonical extracted text. Khi cả hai đường đọc thất bại, status là `failed` và response có:

```json
{
  "data": {
    "id": "01J...",
    "status": "failed",
    "errorCode": "RESUME_EXTRACTION_FAILED",
    "errorMessage": "Không thể đọc nội dung CV. Vui lòng thử lại với file PDF hoặc DOCX rõ hơn."
  }
}
```

`extractedText` và nội dung tài liệu không được trả qua API. Khi resume đã `ready`, `POST /resume-analyses` chỉ enqueue job; client poll `GET /resume-analyses/{id}` cho tới `completed` hoặc `failed`. `analysis.errorCode` chỉ là mã an toàn để hiển thị/xử lý retry, không chứa provider response.

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

`answer.evaluation` giữ các field generic hiện có và có thêm `star` để frontend render STAR coaching khi phù hợp:

```json
{
  "scores": [
    { "criterion": "structure", "score": 70, "evidence": "..." }
  ],
  "feedback": "...",
  "star": {
    "applicable": true,
    "overallScore": 72,
    "situation": { "score": 80, "detected": true, "evidence": "...", "feedback": "..." },
    "task": { "score": 65, "detected": true, "evidence": "...", "feedback": "..." },
    "action": { "score": 85, "detected": true, "evidence": "...", "feedback": "..." },
    "result": { "score": 55, "detected": false, "evidence": "", "feedback": "..." },
    "missingElements": ["result"],
    "strengths": ["..."],
    "coachingTips": ["..."]
  }
}
```

Nếu câu hỏi không phù hợp STAR, `star.applicable=false` và các component có thể là `null`/empty. Frontend không parse prose để suy ra điểm STAR.

Report có thêm `starSummary` khi có ít nhất một answer STAR-applicable:

```json
{
  "starSummary": {
    "applicableAnswers": 2,
    "averageScore": 72,
    "componentAverages": { "situation": 80, "task": 65, "action": 78, "result": 58 },
    "strongestComponent": "action",
    "weakestComponent": "result",
    "recurringIssues": ["result"],
    "coachingPriorities": ["Kết thúc câu trả lời bằng kết quả và tác động cụ thể."]
  }
}
```

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
Order: processing -> pending -> paid -> fulfilled
       pending -> expired | failed
       paid | fulfilled -> refunded (theo DEC-02/BR-07)
Resume: uploaded -> extracting -> ready | failed | deleted
         extracting -> ocr_fallback -> ready | failed
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
