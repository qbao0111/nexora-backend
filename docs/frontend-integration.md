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

Use one hostname consistently (`localhost` is recommended). Browser requests use `credentials: "include"`, including login, refresh and logout. Registration returns a verification-required response without a session; open the email link, then POST its `userId` and `token` to `/api/v1/auth/verify-email` before logging in. The login response contains a short-lived `accessToken`; keep it in memory only and send it as `Authorization: Bearer <accessToken>`. The refresh token is an HttpOnly cookie and is never read or stored by JavaScript.

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

1. `POST /auth/register` (`201`), verify the email, then `/auth/login` (`200`):

   ```json
   { "email": "candidate@example.com", "password": "...", "displayName": "Candidate" }
   ```

Registration returns `{ "email": "...", "verificationRequired": true }` and does not set a refresh cookie. The verification email link contains the `userId` and one-time Identity token; POST them to `/auth/verify-email`. Only after verification does login return `data.accessToken`, set the refresh cookie, and provision the default 100-year Free Plan entitlement with 1 mock interview. Verification retries do not create duplicate entitlements.

For password recovery, call `POST /auth/forgot-password` with `{ "email": "..." }`. The response is intentionally generic for both existing and unknown emails. If a reset email arrives, POST its `userId`, `token` and the new password to `/auth/reset-password`; a successful reset revokes existing sessions, so clear the in-memory access token and show the login screen.

2. `GET /me` (`200`) to hydrate the current user, assigned roles (`["User"]`), and server-owned billing/quota state.

   `POST /me/password` (`204`, Bearer):
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

   To show the user's saved CVs, call `GET /resumes` with the Bearer token. It returns the same safe `ResumeView` metadata in newest-first order and returns `data: []` when the user has no CVs. It never returns extracted text, structured profile, storage keys or provider fields.

   After a resume reaches `ready`, set it as the current CV with `PUT /me/primary-resume` and `{ "resumeId": "..." }`. To clear the current choice, send `{ "resumeId": null }`; the API returns `200` with `data: null` and leaves goals, skills, learning history and resume records unchanged. A missing/foreign resume is `404`; a resume that is not `ready` is `409 RESUME_NOT_READY`.

6. `POST /job-descriptions` (`201`):

   ```json
   { "title": "Backend Developer", "content": "...real job description..." }
   ```

   Save `data.id` as `jobDescriptionId`. For the coordinated CV-analysis flow, send the same stable `Idempotency-Key` used for the subsequent analysis request; this makes JD creation replay-safe. Standalone JD creation may omit it.

## Career Goal / Target Role

Career Goals are user-owned target context shared by future skill, learning and recommendation features. Use `POST /api/v1/career-goals` with `targetRole` and `seniority`; a newly created goal is active and automatically deactivates the user's previous active goal. Use `GET /api/v1/career-goals` to hydrate the full list and choose the item with `active: true`.

`PATCH /api/v1/career-goals/{id}` supports `targetRole`, `seniority`, `industry`, `targetCompany`, `targetJobDescriptionId`, `targetDate` and `active`. Omit a property to leave it unchanged; send `null` for nullable `industry`, `targetCompany`, `targetJobDescriptionId` or `targetDate` to clear it. A referenced Job Description must belong to the same user. The API returns only owner-scoped, non-deleted goals and uses the standard `{ "data": ... }`/`{ "error": ... }` envelopes.

For destructive removal, render a separate `Xóa` action from reversible `Lưu trữ` behavior. Confirm with exactly `Bạn có chắc muốn xóa mục tiêu này? Hành động này không thể hoàn tác.` before sending `DELETE /api/v1/career-goals/{id}` with a unique `Idempotency-Key`. Disable only the affected delete action while pending; on `204`, remove the card from local/cache state without a full-list refetch before rendering. If the request fails, restore the card and show the API error. For archive/reactivate PATCH actions, apply the returned Career Goal to local/cache state immediately, mark any other active goal inactive, and revalidate in the background when useful.

## Candidate practice loop navigation

Use these owner-scoped read endpoints to build the history and setup screens:

```text
GET /api/v1/interviews?page=1&pageSize=20
GET /api/v1/resume-analyses?page=1&pageSize=20
GET /api/v1/job-descriptions
GET /api/v1/job-descriptions/{id}
```

History lists return summary metadata only and use `{ items, page, pageSize,
totalCount, hasNextPage }`. They are newest-first with a deterministic ID
tie-break; `pageSize` is bounded by the server (maximum 100). Do not expect raw
answers, transcripts, CV extraction or provider payloads in these responses.
Foreign detail resources return the normal owner-scoped 404.

To start from a Career Goal, send only the goal-backed context that the user
explicitly chose:

```json
{
  "careerGoalId": "...",
  "interviewType": "technical",
  "difficulty": "medium"
}
```

The server resolves missing role/seniority from the undeleted owner goal,
missing JD from `targetJobDescriptionId`, and missing resume from the owner's
Primary Resume. Explicit role, seniority, resume and JD values override those
defaults only after owner/ready validation. The resolved values are snapshotted
on the new interview, so editing the goal later cannot rewrite history. A
`careerGoalId` is optional, so the legacy explicit start body remains supported.

The first three free primary topics are server-owned and deterministic: Q1 is
`self_introduction`; Q2 reflects the selected mode; Q3 stays in that mode when
possible, with technical mode preferring JD then CV context and self mode using
JD then CV then behavioral when context exists. Behavioral Q2 is
`behavioral_star` and Q3 is `motivation_role_fit`. The free limit and quota
semantics do not change.

After a completed interview has a report, launch a focused new session with a
new idempotency key:

```json
POST /api/v1/interviews/{id}/practice-again
{
  "questionId": "...",
  "focus": "correctness",
  "reason": "repeat_question"
}
```

`questionId` is optional. Valid canonical reasons are `repeat_question`,
`rubric_weakness`, `recommendation` and `manual`; a question retry derives the
question's canonical topic, while a rubric focus is resolved from the source
report/evidence. The result is a new interview with a new normal quota
reservation and nullable source traceability fields. The source interview and
report are never mutated. Reusing the same key and payload replays the same
session; reusing it with a different payload returns `409 IDEMPOTENCY_CONFLICT`.

## Skill Profile

Call `GET /api/v1/skill-profile` with the user's Bearer token after practice evidence is available. The response is a computed read model with `data.competencies` and optional `data.weaknessSignals`; it is empty with `200` when there is no valid scored evidence. Each competency contains `code`, `name`, `category`, integer `score`, `evidenceCount`, `latestEvidenceAt` and deterministic `sources` summaries. Competencies are sorted by category then code.

The profile uses validated CV breakdowns, final interview-report rubric (or answer fallback), detected STAR components and valid completed scenario evaluations. CV gaps/missing keywords are qualitative signals only. The backend returns the canonical current `skillProfile.weaknessSignals`; the frontend should render that collection directly and must not combine historical CV-analysis gaps. For now, it is sourced from the latest valid completed analysis for the user by `CompletedAt ?? UpdatedAt`, with malformed or invalid newer analyses skipped. When Primary CV support is introduced, the scope should become the latest valid completed analysis for that Primary CV. The API is owner-scoped and never returns raw CV text, interview/STAR/scenario answers or full AI payloads. B10 does not add persistence; refresh the endpoint when the underlying evidence changes.

7. `POST /resume-analyses` (`201`, Bearer + idempotency key):

   ```json
   { "resumeId": "...", "jobDescriptionId": "..." }
   ```

   Save `data.id` as `analysisId`. `GET /resume-analyses/{analysisId}` (`200`) returns `queued → processing → completed` or `failed`; a failed result includes `errorCode` when available. Poll with bounded backoff (about 1s, 2s, 3s, 5s; stop after a UI timeout).

## Learning Path

After an active Career Goal exists, call POST /api/v1/learning-path to create its initial path. A repeated call is safe and returns the same path; use GET /api/v1/learning-path for hydration and POST /api/v1/learning-path/refresh after new practice/CV evidence. GET never creates a path. If there is no active goal, the API returns ACTIVE_CAREER_GOAL_REQUIRED; if the active goal has no generated path, GET returns LEARNING_PATH_NOT_FOUND.

The response is a nested milestones[].activities[] read model. Each activity contains a stable id, type, priority, status, order, optional competencyCode/resourceId, and server-computed progress is based only on non-obsolete activities. Types are scenario, external_learning, star_drill, interview and resume_improvement; external_learning has no server-provided resourceId or URL when no published Scenario matches, so the client should render the competency guidance without inventing a link.

To complete an activity, send PATCH /api/v1/learning-path/activities/{activityId} with { "status": "completed" }. Completion is idempotent; clients should not offer an uncomplete transition. When refreshing, retain completed activity IDs and completedAt values, hide/label obsolete pending activities, and add new pending activities. If newer evidence leaves a previously completed competency below threshold, the response contains the preserved completed activity plus one new pending activity for that cycle; repeated refreshes with unchanged evidence do not add more rows. Switching the active Career Goal selects a separate path and does not delete the previous goal's path/history.

## Next Practice Recommendation

Call `GET /api/v1/recommendations/next` with the user's Bearer token after the Learning Path exists. The endpoint is owner-scoped and read-only: it consumes the current user's persisted Learning Path and computed Skill Profile, does not call AI, and does not create or refresh path data. The response is `{ "data": { "reason", "activityType", "resourceId", "estimatedMinutes", "priority", "action", "rationale" } }`; `resourceId` may be `null` for `external_learning` or other activities without a real resource. For evidence-backed recommendations, nullable `rationale` exposes `competencyName`, `evidenceCount` and `hasMoreRecentlyPracticedPeer` so clients can present localized guidance without parsing the legacy English `reason`. For an interview recommendation, nullable `action` contains canonical launch metadata such as `type: "practice_again"`, `reason: "recommendation"`, `sourceInterviewId`, `sourceQuestionId`, `focusTopic` and `suggestedInterviewType` when a completed source interview/report is available.

Only pending activities can be selected. Completed/obsolete activities and stale numeric competency gaps are excluded. Selection is deterministic: lower B11 priority, stronger B10 evidence, less recent practice, weaker current score, Learning Path order, then activity ID. Recent practice is the newer of B10 `latestEvidenceAt` and completed B11 activity timestamps for the same competency. If a valid path has no pending candidate, the API returns `200` with `data: null`. If no active goal/path exists, handle the existing B11 `ACTIVE_CAREER_GOAL_REQUIRED`/`LEARNING_PATH_NOT_FOUND` errors. The duration is server-owned: scenario/interview/external learning 20 minutes, star drill/resume improvement 15 minutes.

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
   While active, use `data.questionPreparationState` (`ready|processing|failed`) to
   show whether the next server-owned question is available. If it is `failed`,
   paid/unlimited users may retry with `POST /interviews/{id}/questions/retry`
   and a new `Idempotency-Key`; free users must be sent through upgrade instead.

   The legacy explicit body remains valid. To start from a Career Goal, send
   `careerGoalId` with `interviewType` and `difficulty`; role, seniority, resume
   and JD may be omitted and are resolved server-side as documented above.

3. For each question, `POST /interviews/{id}/answers` (`200`, new idempotency key):

   ```json
   { "questionId": "...", "content": "...", "durationSeconds": 45 }
   ```

   Render `data.nextQuestion` when present; stop collecting answers when `data.isComplete` is `true`.
   During an `active` session, `GET /interviews/{id}` always reports
   `data.resultState: "collecting"`, including when an answer evaluation failed.
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
2. `GET /api/v1/scenarios/categories` (`200`, Bearer): returns active tracks/categories for grouping the library. Use the returned `slug` as the `category` filter.
3. `GET /api/v1/scenarios/progress` (`200`, Bearer): returns the user's scenario progression, including track/category aggregates, competency aggregates, difficulty aggregates, latest/best score and `recommendedDifficulty`. Only completed attempts with a valid `overallScore` contribute to score aggregates. A latest completed score of `80+` advances one difficulty level (`easy → medium → hard`); lower scores keep the latest level. With no completed attempt, the recommendation is `easy`.
4. `GET /api/v1/scenarios/{id-or-slug}` (`200`, Bearer): fetches scenario details bao gồm toàn bộ nội dung markdown (`content`).
5. `POST /api/v1/scenario-attempts` (`201`, Bearer + `Idempotency-Key`):
   ```json
   { "scenarioId": "..." }
   ```
   Creates or returns the user's active draft attempt.
6. `POST /api/v1/scenarios/{scenarioId}/retry` (`201`, Bearer + `Idempotency-Key`): creates a new draft for an already finished scenario attempt. It returns `409 SCENARIO_ATTEMPT_IN_PROGRESS` when the latest attempt is still a draft, queued or processing.
7. `GET /api/v1/scenarios/{id-or-slug}/attempts` (`200`, Bearer): returns this user's newest-first attempt history for one scenario. Each completed attempt includes `overallScore`, `previousScore`, `scoreDelta` and `improved`; failed/incomplete attempts do not affect score comparison. The top-level `comparison` compares the latest two usable attempts with `currentScore`, `previousScore`, `delta` and `improved`. Raw evaluation JSON is intentionally not included in this history response.
8. `POST /api/v1/scenario-attempts/{attemptId}/submit` (`202`, Bearer + `Idempotency-Key`):
   ```json
   { "answer": "Detailed structured answer addressing the prompt..." }
   ```
   Reserves 1 scenario quota event and queues background AI evaluation.
9. Poll `GET /api/v1/scenario-attempts/{attemptId}` (`200`):
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

`GET /api/v1/progress/dashboard` uses the same Bearer and `progress_analytics` entitlement and adds the B13 dashboard read model without changing the legacy response. The response keeps the legacy progress payload under `historicalStats` and adds:

- `readiness`: B10 equal-weight mean score rounded away from zero, assessed/evidence/priority-gap counts, qualitative weakness count and latest evidence timestamp. With no scored competencies, `score` is `null`.
- `weakestCompetencies`: at most five B10 competencies, ordered by score, evidence strength, recency and ordinal code.
- `recentImprovements`: only positive consecutive interview-score deltas proven by existing comparable history, newest first.
- `weeklyCompletedActivities`: completed ResumeAnalysis, InterviewSession, ScenarioAttempt, StarAttempt and LearningPathActivity counts from Monday 00:00 UTC through the current UTC `TimeProvider` instant. `total` equals the displayed category sum.
- `nextRecommendedPractice`: the B12 recommendation, or `null` when the user has no active Career Goal or has no Learning Path yet. Other errors remain errors.

The endpoint is read-only, uses no AI and returns no raw CV, answer, STAR or scenario content. All values are owner-scoped. An entitled empty user receives a valid empty dashboard; the legacy `/api/v1/progress` contract remains unchanged.

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
