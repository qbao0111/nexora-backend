# Nexora frontend integration

This is the short browser handoff for the internal Development API. The frontend calls only Nexora REST endpoints; it never calls Gemini, the payment adapter, the Worker, PostgreSQL or storage directly.

## Connection and auth

```text
API origin: http://localhost:5088 (Local Development)
API base:   http://localhost:5088/api/v1
Staging:    https://<render-host>/api/v1 (Render Free Staging, see docs/render-staging.md)
Swagger:    http://localhost:5088/swagger       (Development only)
CORS:       http://localhost:3000, http://localhost:5173,
            http://127.0.0.1:3000, http://127.0.0.1:5173,
            https://nexora-staging.vercel.app (when deployed)
```

Use one hostname consistently (`localhost` is recommended). Browser requests use `credentials: "include"`, including register, login, refresh and logout. The response contains a short-lived `accessToken`; keep it in memory only and send it as `Authorization: Bearer <accessToken>`. The refresh token is an HttpOnly cookie and is never read or stored by JavaScript.

When a protected request receives one `401`, call `POST /auth/refresh` once through a shared refresh lock, replace the in-memory token and retry the original request once. If refresh fails, clear memory and show the login screen. Do not loop refresh or put either token in `localStorage`.

Development HTTP uses `SameSite=Lax` and `Secure=false` so localhost works. An HTTPS deployment may configure `SameSite=None` only with `Secure=true` and an explicit CORS allow-list; the API also checks the browser `Origin` on cookie mutation endpoints. Never use wildcard origins with credentials.

All successful JSON responses are `{ "data": ... }`. All handled errors are `{ "error": { "code", "message", "requestId" } }`. Keep the code/requestId for UI and support; never display raw provider responses.

## Idempotency

Async resource completion can now use authenticated SignalR notifications instead of
aggressive polling. Connect to the API origin's `/hubs/realtime`, handle `resourceChanged`,
deduplicate by `eventId`, then refetch the relevant REST resource once. Reconcile on
reconnect and retain slow 15–30 second fallback polling. See the
[complete realtime integration contract](realtime-notifications.md), including the
intentional absence of a report-failure event.

For each user intent, generate one UUID and send it as `Idempotency-Key`. Reuse that key and the identical body when retrying the same request. Generate a new key for a new action. Required mutations are checkout, resume analysis, interview start, answer, interview completion and deletion request. Register/login, upload presign, raw upload, resume finalization and GET requests do not need a key. A standalone `POST /job-descriptions` may omit the header, but the CV-analysis coordinator sends its stable operation key on JD creation so a lost response cannot create a duplicate JD.

## Real CV → JD → analysis flow

1. `POST /auth/register` (`201`) or `/auth/login` (`200`):

   ```json
   { "email": "candidate@example.com", "password": "...", "displayName": "Candidate" }
   ```

   Save `data.accessToken` in memory. The response also sets the refresh cookie. Newly registered users automatically receive the `User` role and a default 100-year Free Plan entitlement with 1 mock interview.

2. `GET /me` (`200`) to hydrate the current user, assigned roles (`["User"]`), and server-owned billing/quota state.

   `POST /me/password` (`200`, Bearer):
   ```json
   { "currentPassword": "...", "newPassword": "..." }
   ```
   Requires authenticated session and minimum 10-character password. Revokes all active refresh tokens and updates security stamp. Frontend should prompt for re-authentication or update memory session.

3. `POST /uploads/presign` (`200`, Bearer):

   ```json
   { "fileName": "resume.pdf", "contentType": "application/pdf", "size": 123456 }
   ```

   Use the browser `File.name`, `File.type` and `File.size`; the user does not type the byte count. Allowed content types are PDF and DOCX and the default limit is 10 MiB.

4. `PUT {uploadUrl}` (`204`, no JSON): send the raw `File` bytes with the declared `Content-Type`. `uploadUrl` is relative to the API origin. Do not send multipart, base64 or a filesystem path.

5. `POST /resumes` (`201`):

   ```json
   { "uploadToken": "data.token" }
   ```

   Save `data.id`. The initial status is `uploaded`. Poll `GET /resumes/{resumeId}` (`200`) until:

   ```text
   uploaded → extracting → ready
                     ↘ ocr_fallback → ready
                     ↘ failed
   ```

   `failed` includes `errorCode: "RESUME_EXTRACTION_FAILED"` and the safe message `Không thể đọc nội dung CV. Vui lòng thử lại với file PDF hoặc DOCX rõ hơn.`. Local text PDFs/DOCX use PdfPig/OpenXML; only suspicious/failed local extraction invokes the single Gemini document fallback.

6. `POST /job-descriptions` (`201`):

   ```json
   { "title": "Backend Developer", "content": "...real job description..." }
   ```

   Save `data.id` as `jobDescriptionId`. For the coordinated CV-analysis flow, send the same stable `Idempotency-Key` used for the subsequent analysis request; this makes JD creation replay-safe. Standalone JD creation may omit it.

7. `POST /resume-analyses` (`201`, Bearer + idempotency key):

   ```json
   { "resumeId": "...", "jobDescriptionId": "..." }
   ```

   Save `data.id` as `analysisId`. `GET /resume-analyses/{analysisId}` (`200`) returns `queued → processing → completed` or `failed`; a failed result includes `errorCode` when available. Poll with bounded backoff (about 1s, 2s, 3s, 5s; stop after a UI timeout).

## Interview and report flow

1. Optional `GET /plans` (`200`) and `POST /checkout-sessions` (`201`, idempotency key) for a paid development entitlement:

   ```json
   { "planPriceId": "10000000-0000-0000-0000-000000000002" }
   ```

   The response is provider-neutral. In Development with SePay, it contains a signed form action:

   ```json
   {
     "orderId": "...",
     "status": "pending",
     "amountMinor": 49000,
     "currency": "VND",
     "provider": "sepay",
     "checkout": {
       "method": "POST",
       "url": "https://pay-sandbox.sepay.vn/v1/checkout/init",
       "fields": [
         { "name": "order_amount", "value": "49000" },
         { "name": "merchant", "value": "..." },
         { "name": "currency", "value": "VND" },
         { "name": "operation", "value": "PURCHASE" },
         { "name": "order_description", "value": "Nexora order NX..." },
         { "name": "order_invoice_number", "value": "NX..." },
         { "name": "signature", "value": "..." }
       ]
     }
   }
   ```

   For SePay, create a temporary HTML form with `method=POST`, `action=checkout.url`, append hidden inputs in exactly the returned `checkout.fields` order, and submit it. Do not use fetch or generate a GET query. Before `form.submit()`, save the order ID:

   ```js
   sessionStorage.setItem("pendingPaymentOrderId", data.orderId);
   form.submit();
   ```

   SePay sends a server-side `POST /api/v1/webhooks/payments/sepay` with `X-Secret-Key`; the browser never receives the SePay SecretKey. The browser return pages (`/payment/success`, `/payment/error`, `/payment/cancel`) are UI hints only. On all three routes, read `pendingPaymentOrderId`, call `GET /api/v1/checkout-sessions/{orderId}`, and treat the backend order status as authoritative. Never grant entitlement from a browser redirect.

   - Success: show “Đang xác nhận thanh toán...”, poll briefly while `pending`, optionally call `POST /api/v1/checkout-sessions/{orderId}/refresh` after a short delay, show success and refetch `/api/v1/me` only when `fulfilled`, then clear `pendingPaymentOrderId`. Show failure if the backend says `failed`.
   - Error: query the backend first because the IPN may already have fulfilled the order; do not immediately mark the order failed locally.
   - Cancel: query the backend without mutating it. If it remains `pending`, let the user leave or start a new checkout.

   A failed order is terminal and requires a new checkout intent. If Development uses `fake`, the existing fake webhook helper remains available for deterministic tests. After payment completion, refetch `/me`.

2. `POST /interviews` (`201`, Bearer + idempotency key):

   ```json
   {
     "role": "Backend Developer",
     "seniority": "junior",
     "interviewType": "behavioral",
     "difficulty": "medium",
     "resumeId": "...",
     "jobDescriptionId": "..."
   }
   ```

   `GET /interviews/{id}` (`200`) moves `starting → active` or `failed`. Render questions only when `active`.

3. For each question, `POST /interviews/{id}/answers` (`200`, new idempotency key):

   ```json
   { "questionId": "...", "content": "...", "durationSeconds": 45 }
   ```

   Render `data.nextQuestion` when present; stop collecting answers when `data.isComplete` is `true`.
   - **Score Scale**: All evaluation scores (rubric `correctness`, `structure`, `completeness`, `clarity` and STAR components) use a uniform `0-100` integer scale with server-computed weighted averages.
   - **Follow-up Reliability**: Even if AI follow-up generation encounters a transient failure or rate limit, the evaluated answer is guaranteed to be persisted and a deterministic Nexora-owned fallback follow-up question is provided in `data.nextQuestion`.

   Behavioral answers include structured STAR coaching at `data.answer.evaluation.star`:

   ```json
   {
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
   ```

   If `applicable=false`, hide the STAR panel and show the existing generic `scores`/`feedback`. Do not parse Gemini prose; the UI can render the structured fields directly.

4. `POST /interviews/{id}/complete` (`202`, new idempotency key, empty body) changes the session to `completing`.

5. Poll `GET /interviews/{id}` and `GET /interviews/{id}/report`. A report `404` means “not ready yet” only while the session is known to be `completing`; otherwise show the error. A successful report (`200`) contains the server score, rubric evidence, strengths, gaps, action plan, optional `starSummary` and coaching disclaimer. `starSummary` appears only when at least one answer was STAR-applicable.

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

6. `GET /dashboard` (`200`) loads persisted interview/report history and billing summary.

## Feature Entitlement Matrix

`GET /api/v1/me` exposes the authoritative snapshot of the current user's entitlement and active feature quotas:

```json
{
  "data": {
    "id": "...",
    "email": "candidate@example.com",
    "displayName": "Candidate",
    "roles": ["User"],
    "billing": {
      "entitlement": {
        "id": "...",
        "planCode": "pro",
        "startsAt": "2026-09-01T00:00:00Z",
        "endsAt": "2026-11-30T00:00:00Z",
        "limit": null,
        "reserved": 0,
        "consumed": 2,
        "available": null,
        "features": [
          {
            "code": "scenario",
            "name": "Thực hành tình huống",
            "enabled": true,
            "limit": 20,
            "reserved": 0,
            "consumed": 3,
            "adjustment": 0,
            "available": 17,
            "unlimited": false
          },
          {
            "code": "progress_analytics",
            "name": "Phân tích tiến độ",
            "enabled": true,
            "limit": null,
            "reserved": 0,
            "consumed": 0,
            "adjustment": 0,
            "available": null,
            "unlimited": true
          }
        ]
      }
    }
  }
}
```

Stable feature codes: `cv_analysis`, `interview`, `scenario`, `star_builder`, `advanced_report`, `progress_analytics`.

## Scenario Library and Practice Flow

### Nguồn dữ liệu và nguyên tắc tích hợp
- **Source of Truth duy nhất**: `GET /api/v1/scenarios` (và `GET /api/v1/scenarios/{slugOrId}`). Frontend **tuyệt đối không sao chép** `scenarios.vi.json` hoặc hardcode nội dung tình huống vào mã nguồn giao diện.
- **Các trạng thái bắt buộc frontend phải xử lý**:
  1. **Loading**: Hiển thị skeleton hoặc spinner khi đang tải danh sách/chi tiết tình huống.
  2. **Empty Library**: Hiển thị empty state rõ ràng khi thư viện chưa có tình huống nào (hoặc bộ lọc không khớp). Mặc dù dữ liệu mẫu được seed trong môi trường Development/Staging, frontend **vẫn phải render đúng trạng thái rỗng** khi `total == 0` hoặc `items` rỗng.
  3. **Loaded**: Render lưới danh sách thẻ tình huống (phân trang, lọc theo danh mục `category`, độ khó `difficulty`, năng lực `competency`, tìm kiếm `search`).
  4. **API Error**: Hiển thị thông báo lỗi thân thiện kèm `requestId` khi gọi API thất bại.

### Quy trình tương tác API
1. `GET /api/v1/scenarios` (`200`, Bearer): lists published scenarios with category, difficulty (`easy`, `medium`, `hard`), and competency. Browsing scenarios does NOT consume quota. Hỗ trợ query params: `category`, `difficulty`, `competency`, `search`, `page`, `pageSize`.
2. `GET /api/v1/scenarios/{id-or-slug}` (`200`, Bearer): fetches scenario details bao gồm toàn bộ nội dung markdown (`content`).
3. `POST /api/v1/scenario-attempts` (`201`, Bearer + `Idempotency-Key`):
   ```json
   { "scenarioId": "..." }
   ```
   Creates or returns the user's active draft attempt.
4. `POST /api/v1/scenario-attempts/{attemptId}/submit` (`202`, Bearer + `Idempotency-Key`):
   ```json
   { "answer": "Detailed structured answer addressing the prompt..." }
   ```
   Reserves 1 scenario quota event and queues background AI evaluation.
5. Poll `GET /api/v1/scenario-attempts/{attemptId}` (`200`):
   Status moves `submitted → processing → completed` (or `failed`). Upon successful evaluation, 1 quota is consumed. If evaluation fails, the reserved quota is voided.
   Evaluation output includes:
   `overallScore`, `dimensions: [{ criterion, score, evidence, feedback }]`, `strengths`, `gaps`, `recommendedApproach`, and `feedback`.

## Standalone STAR Builder Flow

1. `POST /api/v1/star-attempts` (`201`, Bearer + `Idempotency-Key`):
   ```json
   {
     "question": "Kể về một lần bạn xử lý xung đột trong nhóm.",
     "answer": "Khi làm việc tại dự án X, tình huống là... nhiệm vụ của tôi... tôi đã thực hiện... kết quả đạt được..."
   }
   ```
   Reserves 1 `star_builder` quota and queues evaluation.
2. Poll `GET /api/v1/star-attempts/{attemptId}` (`200`):
   Returns structured STAR coaching identical to interview answers (`applicable`, `overallScore`, `situation`, `task`, `action`, `result`, `missingElements`, `strengths`, `coachingTips`).
   Upon successful evaluation, quota is consumed; upon terminal failure, quota is voided.
3. `GET /api/v1/star-attempts` (`200`, Bearer): lists the user's recent standalone STAR attempts.

## Progress Analytics Flow

`GET /api/v1/progress` (`200`, Bearer) is gated by the `progress_analytics` feature entitlement.
Returns privacy-safe user-owned aggregates:
- `completedInterviews` count
- `recentInterviewScores` (last 10 completed sessions)
- `averageInterviewScore`
- `starAverages` (breakdown across situation, task, action, result)
- `completedScenarios` count and `averageScenarioScore`
- `completedStarAttempts` count
- `recentActivity` audit timeline (type, resourceId, timestamp)

## Development shortcut (optional)

`POST /dev/resume-analysis` is **DEVELOPMENT ONLY**. It accepts multipart `File` + `JobDescription` and an idempotency key, then orchestrates the same real upload, storage, extraction, automatic fallback, Worker, PostgreSQL and Gemini services. It is useful for backend debugging; the normal frontend should use the explicit sequence above. The route is not mapped outside Development.

## Status and error handling

| Status | Frontend action |
| --- | --- |
| `201` | Resource/job/session created; save its ID. |
| `202` | Async interview completion accepted; poll. |
| `204` | Raw upload or logout succeeded. |
| `400` | Fix validation/file/type/size; do not retry unchanged. |
| `401` | Refresh once, then require login if refresh fails. |
| `403` | Show ownership/quota/CSRF message. |
| `404` | Resource not found; report polling exception is documented above. |
| `409` | Handle state/not-ready/idempotency conflict; reuse the same key only for the same body. |
| `429` | Wait for `Retry-After`; do not spam. |
| `503` | AI/payment/feature unavailable; show retry later. |

Do not expose internal outbox/Worker details in the UI. For support, record endpoint, HTTP status, `error.code`, `requestId` and time—never tokens, secrets, raw CV/JD text or provider responses.
