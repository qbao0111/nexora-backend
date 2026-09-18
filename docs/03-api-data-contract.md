# API và mô hình dữ liệu

**Status:** Approved implementation baseline  
**Last updated:** 2026-09-18

## Quy ước API

- Base URL: `/api/v1`.
- Xác thực: ASP.NET Core Identity; access token ngắn hạn gửi bằng `Authorization: Bearer` và refresh token rotation qua cookie `HttpOnly`, `Secure`, `SameSite` phù hợp theo ADR-003.
- Response lỗi: `{ "error": { "code": "...", "message": "...", "requestId": "..." } }`.
- `Idempotency-Key` bắt buộc khi duplicated execution không an toàn: checkout/order creation, chargeable resume-analysis/job creation, interview start, official-answer submission, interview completion/report trigger, relevant admin mutation và externally triggered processing khi chưa có provider event ID mạnh hơn. Không yêu cầu cho GET/read-only hoặc upload-intent presign creation. Cùng actor + operation + key + equivalent payload trả kết quả gốc; reuse key với payload khác trả `409 IDEMPOTENCY_CONFLICT`.

## API MVP

Optional realtime invalidation is available through the authenticated SignalR hub
`/hubs/realtime` (outside the REST base path). Its single `resourceChanged` event contains
only event/resource IDs, resource type, status and UTC time. REST response contracts
remain unchanged; see [realtime notifications](realtime-notifications.md) for supported
states, token transport, duplicate handling and reconnect/fallback behavior.

| Method | Endpoint | Mục đích |
| --- | --- | --- |
| POST | `/auth/register` | Tạo account chưa xác minh và gửi email verification; không tạo session. |
| POST | `/auth/verify-email` | Xác minh email bằng `userId` + Identity token; cấp Free entitlement idempotently. |
| POST | `/auth/resend-verification` | Gửi lại email verification với response generic và rate limit theo email. |
| POST | `/auth/forgot-password` | Yêu cầu email đặt lại mật khẩu với response generic; rate limit theo IP và email chuẩn hoá. |
| POST | `/auth/reset-password` | Đặt lại mật khẩu bằng `userId` + Identity token; revoke toàn bộ session sau khi thành công. |
| POST | `/auth/login` | Tạo session sau khi credentials hợp lệ và email đã xác minh. |
| POST | `/auth/refresh` | Xoay refresh token trong cookie. |
| POST | `/me/password` | Đổi mật khẩu với current password; yêu cầu Bearer và revoke toàn bộ session sau khi thành công. |
| GET | `/me` | Profile và entitlement hiện hành. |
| PATCH | `/me/profile` | Cập nhật một phần display name và số năm kinh nghiệm của owner. Email chỉ đọc từ Identity. |
| PUT | `/me/primary-resume` | Chọn, thay thế hoặc bỏ chọn Primary Resume của owner. CV được chọn phải ở trạng thái `ready`. |
| GET | `/me/career-profile` | Đọc aggregate Career Profile computed của owner. |
| GET | `/me/export` | Export allowlisted core profile/billing/practice data của owner; không trả storage key, credential hoặc provider secret. |
| POST | `/me/deletion-requests` | Yêu cầu xoá bất đồng bộ; bắt buộc `Idempotency-Key`, revoke session ngay và trả `202`. |
| GET | `/plans` | Gói, giá, quyền lợi từ server. |
| POST | `/checkout-sessions` | Tạo order và checkout action (form POST của payment provider). |
| GET | `/checkout-sessions/:id` | Đọc trạng thái checkout của owner. |
| POST | `/checkout-sessions/:id/refresh` | Reconcile checkout pending từ provider sandbox khi IPN chậm. |
| POST | `/webhooks/payments/fake` | Nhận webhook fake đã ký cho test deterministic nội bộ. |
| POST | `/webhooks/payments/sepay` | Nhận SePay Sandbox IPN JSON với `X-Secret-Key`, trả `{ "success": true }` khi callback hợp lệ hoặc trùng. |
| POST | `/uploads/presign` | Cấp signed URL upload CV/avatar. |
| POST | `/resumes` | Ghi metadata file sau upload. |
| GET | `/resumes` | Liệt kê CV của owner theo thứ tự mới nhất. |
| GET | `/resumes/:id` | Đọc trạng thái xử lý CV và lỗi an toàn của owner. |
| DELETE | `/resumes/:id` | Xoá CV riêng lẻ theo owner; giữ lịch sử phân tích/phỏng vấn. |
| POST | `/resume-analyses` | Tạo job phân tích CV theo mode `job_targeted` hoặc `field_benchmark`. |
| GET | `/resume-analyses/:id` | Trạng thái/kết quả phân tích. |
| POST | `/career-goals` | Tạo Career Goal/Target Role của owner; goal mới trở thành active. |
| GET | `/career-goals` | Liệt kê Career Goal của owner, active goal đứng trước. |
| GET | `/career-goals/:id` | Đọc một Career Goal của owner. |
| PATCH | `/career-goals/:id` | Cập nhật Career Goal của owner; hỗ trợ đổi active goal. |
| DELETE | `/career-goals/:id` | Soft-delete Career Goal của owner; yêu cầu `Idempotency-Key`. |
| GET | `/skill-profile` | Skill Profile read model của owner, tổng hợp từ evidence hợp lệ. |
| GET | `/learning-path` | Đọc learning path hiện tại của active Career Goal; không tạo dữ liệu. |
| POST | `/learning-path` | Tạo learning path ban đầu cho active Career Goal; lặp lại là idempotent. |
| POST | `/learning-path/refresh` | Reconcile learning path hiện tại với Skill Profile mới nhất. |
| PATCH | `/learning-path/activities/:activityId` | Đánh dấu activity owner là completed. |
| GET | `/recommendations/next` | Đọc một hoạt động pending được chọn deterministic từ Learning Path hiện tại. |
| POST | `/interviews` | Tạo và bắt đầu phiên phỏng vấn. |
| GET | `/interviews` | Lịch sử interview owner-scoped, phân trang bounded, chỉ metadata an toàn. |
| GET | `/interviews/:id` | Đọc session state/question hiện tại của owner. |
| POST | `/interviews/:id/practice-again` | Tạo session luyện lại mới từ interview/report đã hoàn thành; yêu cầu idempotency. |
| POST | `/interviews/:id/answers` | Lưu câu trả lời, đánh giá và mở câu hỏi tiếp theo theo policy server. |
| POST | `/interviews/:id/continue` | Sau khi đạt giới hạn Free, kiểm tra entitlement hiện tại và idempotently tạo câu hỏi trả phí tiếp theo trong cùng session. |
| POST | `/interviews/:id/complete` | Kết thúc, tạo report. |
| POST | `/interviews/:id/report/retry` | Retry report đang `completing`, không charge thêm interview quota. |
| GET | `/interviews/:id/report` | Đọc report immutable của owner khi completed. |
| GET | `/job-descriptions` | Liệt kê Job Description của owner cho setup/history. |
| GET | `/job-descriptions/:id` | Đọc Job Description của owner. |
| GET | `/resume-analyses` | Lịch sử phân tích CV owner-scoped, phân trang bounded, không trả payload riêng tư. |
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

### Xác minh email

`POST /api/v1/auth/register` trả `201` với `{ "data": { "email": "...", "verificationRequired": true } }` và không đặt refresh cookie. Email chứa link frontend dạng `/verify-email?userId={guid}&token={IdentityToken}`; frontend gửi hai giá trị đó tới `POST /api/v1/auth/verify-email`.

Tài khoản chưa xác minh không thể login và không được cấp access/refresh session; login trả `401 EMAIL_NOT_VERIFIED`. Verify thành công đặt `EmailConfirmed=true` và provision Free entitlement trong cùng transaction theo cách idempotent. Gửi lại qua `POST /api/v1/auth/resend-verification` luôn trả thông điệp generic để không tiết lộ email có tồn tại hay không và bị rate limit theo email chuẩn hoá.

### Khôi phục mật khẩu

`POST /api/v1/auth/forgot-password` nhận `{ "email": "..." }` và luôn trả `200` với cùng thông điệp an toàn dù tài khoản tồn tại hay không. Với tài khoản đang hoạt động, email reset chứa link frontend dạng `/reset-password?userId={guid}&token={IdentityToken}`. Endpoint bị giới hạn theo cả IP client và email đã chuẩn hoá.

Frontend gửi `userId`, `token` và `newPassword` tới `POST /api/v1/auth/reset-password`. Token sai hoặc hết hạn trả `400 PASSWORD_RESET_INVALID` với thông điệp chung. Reset thành công đổi password, revoke toàn bộ refresh token và cập nhật security stamp, vì vậy access token và refresh token cũ đều không còn dùng được.

### Đổi mật khẩu khi đã đăng nhập

`POST /api/v1/me/password` yêu cầu Bearer access token và body `{ "currentPassword": "...", "newPassword": "..." }`. Thành công trả `204 No Content`, đổi mật khẩu và revoke toàn bộ refresh token đồng thời cập nhật security stamp; access token cũ cũng không còn hợp lệ. Sai current password trả `401 INCORRECT_CURRENT_PASSWORD`; password mới phải dài 10–128 ký tự và thỏa Identity password policy. `confirmPassword` là validation ở frontend, không gửi lên API.

### Export và xoá dữ liệu cá nhân

`GET /api/v1/me/export` chỉ trả core data thuộc owner; danh sách resume phản ánh library hiện hành và bỏ qua resume đã soft-delete, còn analysis/interview history vẫn được export. `POST /api/v1/me/deletion-requests` tạo audit state `queued → processing → completed|failed`; cùng user và `Idempotency-Key` trả request gốc. Sau khi accepted, access/refresh session hiện tại không còn hợp lệ. Worker xoá private object và personal practice records rồi anonymize Identity account; billing/usage ledger được giữ làm audit theo retention được phê duyệt. Thời hạn retention production vẫn do DEC-03 quyết định.

### Candidate practice loop navigation

The owner-scoped navigation endpoints are:

```text
GET /api/v1/interviews?page=1&pageSize=20
GET /api/v1/resume-analyses?page=1&pageSize=20
GET /api/v1/job-descriptions
GET /api/v1/job-descriptions/{id}
```

Interview and resume-analysis history responses use `{ items, page, pageSize,
totalCount, hasNextPage }`, are newest-first by `createdAt` with an ID
tie-break, and cap `pageSize` at 100. Interview items contain only state,
resolved context metadata, timestamps, answer/question counts, report
availability and nullable practice-again traceability IDs/reason/focus. Resume
analysis items contain mode/status/timestamps, safe context metadata and a safe
error code only. Job Description navigation is owner-scoped; its title and
content are returned because they are required for setup and Career Goal
editing. Foreign resources return 404 and no endpoint returns raw answer,
transcript, CV extraction or provider payloads in history lists.

`POST /api/v1/interviews` keeps the existing explicit body contract and also
accepts:

```json
{
  "careerGoalId": "01J...",
  "interviewType": "technical",
  "difficulty": "medium"
}
```

When `careerGoalId` is present, it must be an undeleted owner goal. Missing role
and seniority come from the goal; missing Job Description comes from
`targetJobDescriptionId`; missing resume comes from the owner's selected
Primary Resume. Explicit role, seniority, resume and JD values override those
defaults only after the same owner/ready checks. The resolved values are
snapshotted on the interview session, so later goal edits do not rewrite
history. A null `careerGoalId` preserves the legacy explicit-start behavior.

`POST /api/v1/interviews/{id}/practice-again` requires a new
`Idempotency-Key` and accepts nullable `questionId`, `focus` and canonical
`reason` (`recommendation`, `rubric_weakness`, `repeat_question` or `manual`).
The source must belong to the caller, be `completed` and have a report. A
source question retry requires an answered source question and derives its
canonical topic; a rubric focus derives the weakest matching topic from the
immutable report/answer evidence. The endpoint creates a new interview and a
new normal quota reservation, records source links and never mutates the source
interview or report. The same key/payload replays the same new session; a
different payload returns `409 IDEMPOTENCY_CONFLICT`.

For an interview recommendation, `GET /api/v1/recommendations/next` may add a
nullable `action` object:

```json
{
  "type": "practice_again",
  "reason": "recommendation",
  "sourceInterviewId": "01J...",
  "sourceQuestionId": null,
  "focusTopic": "correctness",
  "suggestedInterviewType": "behavioral"
}
```

The action is only emitted when a completed source interview/report can be
resolved; its fields are canonical metadata, not free-form semantic input.

### Career Goal / Target Role

Career Goal là resource riêng của user. Tất cả endpoint chỉ query theo authenticated user; một ID thuộc user khác trả `404 CAREER_GOAL_NOT_FOUND`. `DELETE /api/v1/career-goals/{id}` là soft-delete: server đặt `active: false`, ghi `deletedAt` nội bộ và không xoá row, nên Learning Path/history vẫn giữ foreign key hợp lệ. Goal đã xoá không xuất hiện trong các query Career Goal thông thường, không thể `GET`, `PATCH` hoặc reactivate; privacy export vẫn bao gồm dữ liệu owner trước khi account deletion workflow xoá dữ liệu.

`POST /api/v1/career-goals` nhận:

```json
{
  "targetRole": "Backend Developer",
  "seniority": "senior",
  "industry": "Fintech",
  "targetCompany": "Example Bank",
  "targetJobDescriptionId": "00000000-0000-0000-0000-000000000000",
  "targetDate": "2027-06-30"
}
```

`targetRole` bắt buộc, tối đa 160 ký tự; `seniority` bắt buộc, tối đa 40 ký tự và phải là một trong `intern`, `entry`, `junior`, `mid`, `senior`, `lead`, `staff`, `principal`, `manager`, `director`, `executive` (các alias `entry-level`/`mid-level` được chuẩn hoá). `industry` tối đa 120 ký tự và `targetCompany` tối đa 160 ký tự; chuỗi optional rỗng được chuẩn hoá thành `null`. `targetDate` dùng định dạng ISO `YYYY-MM-DD`.

Goal mới luôn `active: true` và transaction sẽ chuyển goal active trước đó của cùng user thành inactive. Database cũng có partial unique index để chỉ cho phép một active goal trên mỗi user. Nếu `targetJobDescriptionId` được gửi, JD phải tồn tại và thuộc authenticated user; nếu không, API trả `404 JOB_DESCRIPTION_NOT_FOUND`.

`GET /api/v1/career-goals` trả các goal chưa xoá của owner, sắp xếp active trước rồi tới mới nhất. `PATCH /api/v1/career-goals/{id}` nhận các field editable: `targetRole`, `seniority`, `industry`, `targetCompany`, `targetJobDescriptionId`, `targetDate`, `active`. Field bị bỏ qua giữ nguyên; nullable field gửi `null` để clear; `active: true` áp dụng cùng invariant một-goal-active. Response thành công dùng envelope `{ "data": { ... } }` và gồm `id`, target fields, `active`, `createdAt`, `updatedAt`.

`DELETE /api/v1/career-goals/{id}` yêu cầu header `Idempotency-Key` hợp lệ và trả `204 No Content`. Cùng authenticated user, operation và key được retry an toàn; dùng lại key cho goal khác trả `409 IDEMPOTENCY_CONFLICT`. Goal không tồn tại, đã bị ẩn do soft-delete hoặc thuộc user khác trả `404 CAREER_GOAL_NOT_FOUND`. `DELETE` không phải thao tác archive/reversible: API không cung cấp đường reactivation cho goal đã bị xoá.

### Skill Profile

`GET /api/v1/skill-profile` là computed read model, không tạo bảng/entity hay migration mới. Endpoint yêu cầu Bearer authentication và chỉ đọc evidence thuộc authenticated user. Response thành công dùng envelope chuẩn:

```json
{
  "data": {
    "competencies": [
      {
        "code": "interview.clarity",
        "name": "Clarity",
        "category": "interview",
        "score": 82,
        "evidenceCount": 3,
        "latestEvidenceAt": "2026-09-11T09:00:00+00:00",
        "sources": [
          {
            "sourceType": "interview",
            "evidenceCount": 3,
            "latestEvidenceAt": "2026-09-11T09:00:00+00:00"
          }
        ]
      }
    ],
    "weaknessSignals": [
      {
        "sourceType": "cv_analysis",
        "label": "SQL",
        "latestEvidenceAt": "2026-09-11T08:00:00+00:00"
      }
    ]
  }
}
```

Numeric competencies use only validated structured scores from completed owner-scoped `ResumeAnalysis` breakdowns, canonical final `InterviewReport` rubrics (with answer-rubric fallback when no valid report exists), applicable/detected STAR components, and completed valid `ScenarioAttempt` evaluations. Codes are deterministic: `resume.<dimension>`, `interview.<criterion>`, `behavioral.<component>` and `scenario.<normalized-competency>`; `resume.clarity` and `interview.clarity` remain separate. Scores are the equal-weight arithmetic mean per code with one final `Math.Round(..., MidpointRounding.AwayFromZero)` operation. `evidenceCount`, `latestEvidenceAt` and `sources` provide traceability.

CV `Gaps` and `MissingKeywordsOrSkills` may appear as qualitative `weaknessSignals`, but never manufacture a numeric competency score. For now, these signals come only from the latest valid completed owner-scoped `ResumeAnalysis`, selected by descending `CompletedAt ?? UpdatedAt`; malformed or semantically invalid newer analyses are skipped in favor of the next latest valid analysis. Labels are trimmed, deduplicated case-insensitively and ordered deterministically. When Primary CV support is introduced, this scope becomes the latest valid completed analysis for that Primary CV. Invalid/malformed individual evidence is skipped; no valid scored evidence returns `200` with an empty `competencies` array. The response never exposes raw CV text, interview answers, STAR quotes, scenario answers or full AI output.

### Learning Path

Learning Path là dữ liệu persisted, owner-scoped và luôn gắn với một Career Goal. Các endpoint yêu cầu Bearer authentication và trả envelope chuẩn { "data": ... }. GET /api/v1/learning-path chỉ đọc path của Career Goal đang active; nếu goal chưa có path, trả 404 LEARNING_PATH_NOT_FOUND, còn nếu user chưa có active goal trả 400 ACTIVE_CAREER_GOAL_REQUIRED. GET không tự tạo hoặc refresh dữ liệu.

POST /api/v1/learning-path tạo path ban đầu từ ISkillProfileService; lần đầu trả 201, gọi lặp khi path đã tồn tại trả 200 cùng id. POST /api/v1/learning-path/refresh tạo path nếu chưa có hoặc reconcile path hiện tại và trả 200. Cả hai thao tác được serialize theo owner và database unique key (user_id, career_goal_id).

Response có id, careerGoalId, status, timestamps, progress và milestones. progress gồm completedActivityCount, totalActivityCount, percentage; activity obsolete không nằm trong mẫu số, path không có activity có 0/0 và percentage: 0. Milestone/activity được sắp xếp deterministic bằng server order. Activity trả id, type, title, description, competencyCode, nullable resourceId/externalUrl, priority, status, order và nullable completedAt.

Planner map gap số có score < 60 vào priority 1 và 60..74 vào priority 2; score >= 75 không tạo numeric gap. scenario.<competency> tạo scenario activity với Scenario đã published tương ứng; nếu chưa có resource phù hợp, gap vẫn được biểu diễn bằng external_learning với cùng competencyCode, priority/milestone và resourceId/externalUrl đều null. behavioral.*, interview.* và resume.* lần lượt tạo star_drill, interview và resume_improvement. CV qualitative signal chỉ tạo supporting resume_improvement, không tạo điểm số và không lấn át numeric gaps. B11 không tự sinh URL hoặc Scenario ID.

PATCH /api/v1/learning-path/activities/{activityId} nhận { "status": "completed" }. Chỉ transition pending -> completed được phép; lặp lại transition completed là idempotent, không có API đưa completed trở lại pending. Activity obsolete trả 409; ID của owner khác trả 404 LEARNING_PATH_ACTIVITY_NOT_FOUND. Refresh không xóa activity đã completed, giữ nguyên id/completedAt, chuyển pending gap đã được giải quyết thành obsolete và thêm gap mới ở trạng thái pending. Nếu cùng competency vẫn là gap nhưng có LatestEvidenceAt mới hơn CompletedAt, refresh giữ activity completed cũ và tạo đúng một activity pending cho learning cycle mới; refresh lặp lại với cùng evidence không nhân bản activity.

### Next Practice Recommendation

`GET /api/v1/recommendations/next` yêu cầu Bearer authentication và chỉ đọc Learning Path cùng Skill Profile của authenticated user. Endpoint là computed read model: không gọi AI, không tạo hoặc cập nhật Learning Path, không thêm entity/table/DbSet/migration và không nhận `userId` từ client.

Response thành công dùng envelope chuẩn và trả `data` là `null` khi path hợp lệ nhưng không còn activity `pending`; trạng thái rỗng này là `200`, không phải lỗi. Nếu user chưa có active Career Goal hoặc active goal chưa có Learning Path, endpoint giữ nguyên lỗi B11 tương ứng `ACTIVE_CAREER_GOAL_REQUIRED` hoặc `LEARNING_PATH_NOT_FOUND`.

Khi có ứng viên, response chỉ gồm `reason`, `activityType`, nullable `resourceId`, `estimatedMinutes` và `priority`. Chỉ activity `pending` được xét; `completed`, `obsolete`, và numeric activity không còn là gap hiện tại của B10 (không có competency match hoặc score >= 75) bị loại. Scenario chỉ trả resource ID đã có trong B11; `external_learning` và activity không có resource trả `resourceId: null`.

Ranking rule deterministic theo thứ tự: `priority` tăng dần; `EvidenceCount` giảm dần; `LastPracticeAt` tăng dần; score tăng dần; milestone `SortOrder`; activity `SortOrder`; activity ID. Với competency, `LastPracticeAt` là thời điểm mới nhất giữa B10 `LatestEvidenceAt` và `CompletedAt` của activity B11 đã completed cùng competency. Với qualitative resume improvement không có competency code, ranking chỉ dùng timestamp ổn định của Learning Path và `EvidenceCount = 0`/không có score để làm fallback. `estimatedMinutes` là server policy: scenario 20, star_drill 15, interview 20, resume_improvement 15, external_learning 20. `reason` được tạo deterministic từ tên activity/competency, priority, evidence count và tín hiệu recency; không chứa raw CV, answer, STAR hoặc AI output.

### Primary Resume and Career Profile

`GET /api/v1/resumes` requires Bearer authentication and returns `{ "data": [ ... ] }` containing only the authenticated user's non-deleted `ResumeView` metadata (`id`, `fileName`, `contentType`, `size`, `status`, `createdAt`, and safe failure fields). Results are ordered by `createdAt` descending and then `id` descending. The response never includes extracted CV text, structured profile, storage key, or provider fields; an account with no active resumes receives an empty array. `GET /api/v1/resumes/{id}` returns the same safe owner-scoped view, or opaque `404 NOT_FOUND` for unknown, foreign, or soft-deleted IDs.

`DELETE /api/v1/resumes/{id}` requires Bearer authentication and is owner-scoped. It returns `204 No Content`; an unknown or foreign ID returns the same opaque `404 NOT_FOUND`, while repeating DELETE for an already soft-deleted resume owned by the caller also returns `204`. In one transaction, the service sets the resume tombstone and clears `user_profiles.primary_resume_id` only when it points to that resume. New resume selections, analyses, interviews and profile/evidence computations exclude tombstoned resumes. Existing `resume_analyses`, `interview_sessions`, snapshots and reports remain intact as history; account deletion retains its independent full-account cleanup behavior.

Physical storage removal is asynchronous and provider-neutral: a worker calls `IStorageProvider.DeleteAsync` with the server-owned stored key, persists completion/attempt/next-attempt state, and retries failures with capped exponential backoff without a terminal attempt limit. Cleanup is deferred while that resume's extraction job is pending or processing; a queued analysis or interview that has not started before deletion is finalized without starting new AI work, with any quota reservation voided. The extraction/analysis/interview job that crossed its start gate before deletion may finish but cannot make a tombstoned resume visible again. The resume emits the existing `resourceChanged` event with status `deleted`; clients invalidate/prune that resume and refetch `/api/v1/me/career-profile` because its Primary Resume may have been cleared. The client should treat `404` from the canonical resume refetch as deletion, not as a transient failure.

An active interview may retain its historical `resumeId` after deletion, but the deleted Resume is no longer usable current AI context. Future answer evaluation, automatic next-question, paid continuation and follow-up calls pass no deleted `ResumeProfile` and select topics as if resume context were unavailable. Existing questions, answers, evaluations, reports and the historical linkage remain unchanged. `Practice Again` creates a new session through the normal non-deleted resume validation and therefore rejects an inherited tombstoned Resume.

`PUT /api/v1/me/primary-resume` requires Bearer authentication and accepts `{ "resumeId": "..." }` or `{ "resumeId": null }`. A non-null resume must belong to the authenticated user, be non-deleted, and have status `ready`; missing, foreign, deleted, or unavailable resumes are rejected without revealing ownership. Sending `null` clears the current Primary Resume and returns `200` with `{ "data": null }`; the operation is idempotent and does not change career goals, skill evidence, learning-path history, or resume records. The mutation updates one nullable `primaryResumeId` in `user_profiles` inside a transaction, so selecting the same resume is safe and replacing the previous resume never creates multiple primary rows. The MVP does not auto-select the first resume; the client selects it explicitly after extraction completes.

`PATCH /api/v1/me/profile` requires Bearer authentication and accepts a partial `{ "displayName": "...", "yearsOfExperience": 2 }` body. A supplied display name is trimmed, non-empty and at most 120 characters; a supplied years value is an integer from 0 through 60. Omitted fields remain unchanged, repeated values are safe, and email is not writable. The response preserves the existing `UserResponse` fields (`id`, `email`, `displayName`, `roles`, and nullable `billing`, which remains null for this update) and additively includes nullable `yearsOfExperience`; identity fields are always taken from the authenticated account.

`GET /api/v1/me/career-profile` is a computed, owner-scoped aggregate read model. Its standard envelope contains `profile` (`userId`, read-only `email`, `displayName`, nullable `yearsOfExperience` and nullable `avatarUrl`), nullable `primaryResume` (file metadata plus the latest analysis for that resume only), nullable `activeCareerGoal`, `skillProfileSummary` (at most five competencies and five weakness signals), nullable `learningPath` for the active goal, and `onboarding`. `onboarding.isComplete` is true only when `hasDisplayName`, `hasYearsOfExperience`, `hasPrimaryResume` and `hasActiveCareerGoal` are all true. Avatar, JD, target company, skill evidence and learning path are not required for setup. Career Goal, Resume, Skill Profile, and Learning Path remain separate resources; this endpoint does not create or refresh data and never returns raw CV, answer, transcript, AI payload, storage key, or provider metadata.

### Progress Dashboard v2

`GET /api/v1/progress/dashboard` yêu cầu Bearer authentication và cùng entitlement `progress_analytics` với legacy `GET /api/v1/progress`. Nó trả computed read model trong envelope `{ "data": ... }`; `GET /api/v1/progress` và toàn bộ field/semantics của `ProgressResponse` không thay đổi.

Response gồm `readiness`, `weakestCompetencies`, `recentImprovements`, `weeklyCompletedActivities`, `nextRecommendedPractice` và `historicalStats`. `historicalStats` giữ nguyên response legacy để frontend cũ không bị break.

`readiness.score` là arithmetic mean equal-weight của các B10 scored competencies, làm tròn đúng một lần bằng `Math.Round(mean, MidpointRounding.AwayFromZero)`. Khi không có competency, score là `null`, assessed/evidence/priority-gap đều bằng 0; qualitative weakness count và latest evidence timestamp vẫn phản ánh B10 signals. `priorityGapCount` đếm score strictly below B11 threshold `75`, còn `evidenceCount` là tổng EvidenceCount của B10.

`weakestCompetencies` chỉ dùng B10 competencies, tối đa 5 item và sắp xếp theo score tăng, EvidenceCount giảm, LatestEvidenceAt giảm, rồi code ordinal tăng. Qualitative weakness không được chuyển thành competency giả hoặc score giả.

`recentImprovements` chỉ dùng `IProgressService.ProgressView.RecentInterviewScores`. Các score được so sánh theo cặp consecutive chronologically; chỉ delta dương mới được trả, với `kind: "interview"`, current interview ID, previous/current score, delta và thời điểm current score. Kết quả mới nhất đứng trước và tối đa 5 item. Không suy diễn improvement từ text hoặc từ scenario/STAR khi không có chuỗi comparable canonical.

`weeklyCompletedActivities` dùng UTC calendar week: từ Monday 00:00 UTC tới thời điểm UTC hiện tại do `TimeProvider` cung cấp. Chỉ completion timestamp của các row thuộc authenticated user được tính: completed ResumeAnalysis, InterviewSession, ScenarioAttempt, StarAttempt và LearningPathActivity. Failed, queued, processing, draft, obsolete và pending bị loại; `total` là tổng các breakdown.

`nextRecommendedPractice` tái sử dụng B12 `INextPracticeRecommendationService` và giữ nguyên ranking/duration của B12. Chỉ hai lỗi absence đã biết là `ACTIVE_CAREER_GOAL_REQUIRED` và `LEARNING_PATH_NOT_FOUND` được chuyển thành `null`; lỗi database, cancellation, authorization và business error khác vẫn propagate. Empty/entitled user vẫn nhận dashboard hợp lệ với readiness null, các list rỗng, weekly zero và next recommendation null.

Dashboard không gọi AI, không đọc raw CV/answer/STAR/scenario content, không thêm persistence/DbSet/migration/ModelSnapshot và không mutate dữ liệu. Weekly queries filter `UserId` tại database boundary; Learning Path activity scope qua `LearningPath.UserId`.

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

Với `Storage:Provider=r2`, client gọi `POST /uploads/presign`; server tạo intent bền vững với token capability ngẫu nhiên (chỉ hash token được lưu), cấp một signed HTTPS `uploadUrl` ngắn hạn cho đúng bucket/key private; client PUT raw bytes trực tiếp tới URL đó và không gửi bearer token của Nexora tới storage. Sau đó client gọi `POST /resumes` với `uploadToken`; endpoint này là finalize/confirm: server kiểm tra owner, expiry, object tồn tại, kích thước thực tế, chữ ký/container PDF/DOCX và checksum trước khi tạo metadata resume. Finalize lặp lại sau khi thành công trả cùng metadata và không tạo thêm object/resume/outbox. Với `Storage:Provider=local` (Development/Testing), `uploadUrl` vẫn trỏ tới `PUT /api/v1/uploads/{token}` và provider local giữ flow server-side hiện hữu.

Response resume ban đầu có `status: "uploaded"`; client poll `GET /api/v1/resumes/{id}` hoặc reconcile qua realtime cho tới `ready` hoặc `failed`. Worker luôn mở object qua `IStorageProvider`, dùng `extracting` cho local PdfPig/OpenXML, `ocr_fallback` khi quality gate yêu cầu document fallback Gemini, rồi `ready` khi đã lưu canonical extracted text. Trước extraction worker đối chiếu size + SHA-256 với `StoredFile`; object bị thay đổi sau finalize sẽ không được coi là resume hợp lệ. Khi cả hai đường đọc thất bại, status là `failed` và response có:

Upload giữ giới hạn mặc định 10 MiB và kiểm tra cả kích thước byte, cặp extension/MIME, chữ ký và container tài liệu trước khi lưu private object. PDF có container hợp lệ nhưng bị mã hóa vẫn được lưu để quyết định khả năng đọc/OCR ở boundary extraction; lỗi mật khẩu hoặc không đọc được không bị biến thành lỗi format upload. Lỗi upload dùng mã ổn định: `UPLOAD_SIZE_ZERO`, `UPLOAD_SIZE_EXCEEDED`, `UPLOAD_TYPE_UNSUPPORTED`, `UPLOAD_SIZE_MISMATCH`, `UPLOAD_SIGNATURE_INVALID`, `UPLOAD_CONTAINER_INVALID`, `UPLOAD_CONTAINER_LIMIT`, `UPLOAD_OBJECT_NOT_FOUND`, `UPLOAD_DIRECT_UPLOAD_UNSUPPORTED`, `UPLOAD_INTENT_EXPIRED`, `UPLOAD_INTENT_INVALID` và `UPLOAD_FINALIZE_CONFLICT`. Các lỗi validation này trả HTTP 400 theo contract hiện tại; intent/object không tồn tại khi finalize trả `UPLOAD_NOT_FOUND` hoặc `UPLOAD_OBJECT_NOT_FOUND`/404. Signed URL không được log hoặc lưu lại; object luôn private và storage key là dữ liệu server-owned.

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

### Resume analysis v2

`POST /api/v1/resume-analyses` requires an `Idempotency-Key` and an explicit mode. `resumeId`, `careerGoalId`, the mode context fields and `jobDescriptionId` are optional request inputs; the server resolves missing values from the owner's selected Career Profile context without inferring the mode from whether `jobDescriptionId` is present:

```json
{
  "resumeId": "01J...",
  "careerGoalId": "01K...",
  "mode": "job_targeted",
  "jobDescriptionId": "01J..."
}
```

```json
{
  "mode": "field_benchmark",
  "careerGoalId": "01K..."
}
```

The server resolves each missing value independently using this precedence: explicit request value, selected `careerGoalId` when supplied (otherwise the active non-deleted Career Goal), then the user's Primary Resume for `resumeId` where applicable. Career Goals have no canonical Resume relation, so resume fallback always uses `UserProfile.PrimaryResumeId`. A selected goal's canonical `TargetJobDescriptionId` is inherited only by `job_targeted`; `field_benchmark` continues to reject JD context and uses only the goal's `industry`, `targetRole` and `seniority`. `job_targeted` requires an owner-owned job description and rejects benchmark fields. `field_benchmark` rejects a job description and requires `industry`, `targetRole` and `seniority` after server-side trimming and length checks. Both modes require a `ready` resume; ownership, entitlement and quota remain server-authoritative.

The resolved context is snapshotted when the analysis is created: the effective resume is stored in `ResumeId`, the effective JD/version is stored for job-targeted runs, and the effective benchmark fields are stored in `ContextJson`. Later Primary Resume or Career Goal changes do not rewrite that history. The `201` response contains the queued job's `mode`, context snapshot, resume/JD versions (the JD version is nullable for field benchmark), analysis model/prompt/schema/rubric versions and the cached profile's model/prompt/schema versions. Job-targeted results contain `matchScore` and the five required breakdown dimensions. Field-benchmark results contain `readinessScore` and the six required dimensions. Scores are integer 0-100 values; matched/missing skill arrays may be empty, while coaching and section-feedback arrays remain non-empty; mode mismatches, unknown dimensions and ungrounded content fail strict semantic validation after at most one repair attempt. `GET /resume-analyses/{id}` remains the authoritative status/result read.

The idempotency fingerprint includes the effective resolved resume, mode, JD and every effective context field. Equivalent replays return the same analysis/outbox/reservation; reusing a key after inherited defaults resolve to different effective input returns `409 IDEMPOTENCY_CONFLICT`. Worker retries reuse the cached profile and never create a second analysis or quota charge.

Email verification provisions exactly one account-level Free `cv_analysis` allowance with limit `1`. The allowance is shared by `job_targeted` and `field_benchmark`, so using one mode exhausts the other; a second genuine analysis is rejected with `403 FEATURE_QUOTA_EXCEEDED` before an analysis, reservation or outbox job is persisted. The normal ledger remains immutable: reserve before enqueue, consume only after a usable result is persisted, and void a reservation when processing fails before a usable result. Equivalent idempotent replays return the original analysis and do not reserve or consume again; paid limits continue to come from the server-owned plan/entitlement snapshot.

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

Voice/browser STT boundary (A10): the browser may turn speech into an editable
transcript, but the user must confirm the final text before submission. The
backend receives only `content` from this request and treats it exactly like a
typed answer; it does not receive or store raw STT transcript, audio, provider
metadata, or an alternate answer record. `durationSeconds` remains optional
answer/session timing supplied by the client and is not proof of an audio
recording. Evaluation, follow-up context, history, and reports use the
persisted final `content` value.

Response có answer đã lưu và question tiếp theo hoặc `isComplete: true`. Một question chỉ nhận một answer chính thức trừ khi endpoint revision được định nghĩa riêng.

Mỗi phần tử trong `interview.questions` là server-owned và có thêm metadata lineage:

```json
{
  "id": "01J...",
  "sequence": 1,
  "kind": "primary",
  "topic": "self_introduction",
  "parentQuestionId": null,
  "content": "...",
  "createdAt": "2026-09-10T00:00:00Z"
}
```

`kind` chỉ nhận `primary` hoặc `followup`; `topic` là semantic focus và
`parentQuestionId` bắt buộc đối với follow-up, trỏ tới một câu hỏi trước trong
cùng session. `sequence` chỉ dùng để sắp xếp, không được dùng để suy ra
follow-up. A7 dành các topic primary miễn phí theo thứ tự
`self_introduction`, `behavioral_star`, `motivation_role_fit`; sau Q3, response
trả `nextQuestion: null` và continuation server-owned. `continuation.state` có
thể là `in_progress`, `upgrade_required` hoặc `max_questions_reached`; chỉ
`upgrade_required` cho phép gọi endpoint `/interviews/{id}/continue` sau khi
entitlement đã được cập nhật. Endpoint này yêu cầu `Idempotency-Key`, không
nhận `paid`, `plan` hay quota từ client, không tạo session mới và không gọi AI
trước khi server re-check entitlement. Câu hỏi trả phí đầu tiên là một
`primary` với topic được chọn từ session context; một `followup` chỉ được tạo
khi evaluation STAR của câu hỏi behavioral trả phí cho thấy thiếu thành phần
và luôn kế thừa topic/parent rõ ràng. Retry cùng key trả cùng session/question;
key khác payload trả `409 IDEMPOTENCY_CONFLICT`.

`answer.evaluation` giữ các field generic hiện có và có thêm coaching theo từng câu trả lời. Các field `strengths`, `improvements`, `improvedAnswer` và nullable `sampleAnswer` được tạo trong cùng một `interview.evaluate` call với rubric/STAR; server chỉ lưu core evaluation sau khi schema và semantic validation thành công. `sampleAnswer` được kiểm tra độc lập: nội dung thiếu/sai schema, framework không hỗ trợ, hoặc thiếu phần bắt buộc được bỏ thành `null`/không có field, không làm hỏng core evaluation hợp lệ và không tạo thêm AI call.

```json
{
  "scores": [
    { "criterion": "structure", "score": 70, "evidence": "..." },
    { "criterion": "correctness", "score": 75, "evidence": "..." },
    { "criterion": "completeness", "score": 60, "evidence": "..." },
    { "criterion": "clarity", "score": 80, "evidence": "..." }
  ],
  "feedback": "...",
  "scoreScale": "0-100",
  "strengths": ["Điểm mạnh có bằng chứng trong câu trả lời"],
  "improvements": ["Bổ sung một ví dụ hoặc kết quả cụ thể nếu có"],
  "improvedAnswer": "Phiên bản trả lời được diễn đạt rõ hơn nhưng chỉ dùng facts ứng viên đã nêu.",
  "sampleAnswer": {
    "framework": "star",
    "situation": "Ví dụ giả định: Một trang danh sách nội bộ phản hồi chậm khi dữ liệu tăng.",
    "task": "Tôi phụ trách tìm nguyên nhân và đề xuất cải thiện.",
    "action": "Tôi kiểm tra truy vấn, thêm phân trang và đo lại luồng chính cùng nhóm.",
    "result": "Tải trang ổn định hơn; nhóm ghi lại cách kiểm tra để dùng cho các màn hình tương tự.",
    "fullAnswer": "Ví dụ minh họa, không phải trải nghiệm ứng viên: Trong một dự án giả định, một trang danh sách nội bộ phản hồi chậm khi dữ liệu tăng. Tôi phụ trách tìm nguyên nhân, kiểm tra truy vấn và phối hợp thêm phân trang. Sau đó, tải trang ổn định hơn và nhóm dùng lại cách kiểm tra cho các màn hình tương tự."
  },
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

`candidateAnswer` là nguồn facts duy nhất về điều ứng viên thực sự đã nói/làm. `improvedAnswer` là bản viết lại có căn cứ, không thêm thành tích, trách nhiệm, công nghệ hay số liệu. `sampleAnswer` là ví dụ dạy cấu trúc, tách biệt hoàn toàn: có thể dùng bối cảnh/chi tiết giả định nếu được trình bày rõ là ví dụ, nhưng không được đưa facts đó vào candidate evidence, rubric scores, strengths, report transcript/evidence, skill profile, progress hay recommendations. UI phải giữ nhãn minh họa riêng để không gây hiểu nhầm đây là lịch sử của ứng viên.

`framework` chỉ nhận `star`, `self_intro`, `technical`, `direct`. `star` dành cho câu hỏi hành vi, trải nghiệm quá khứ, xung đột, lãnh đạo, làm việc nhóm, giải quyết vấn đề hoặc thành tựu; khi chọn, `situation`, `task`, `action`, `result` phải đều không rỗng. `self_intro` dùng cho giới thiệu, động lực hoặc mức độ phù hợp; `technical` cho kiến thức kỹ thuật; `direct` cho câu hỏi khác. Ba framework không phải STAR phải trả các phần STAR là `null`, không ép mẫu theo STAR. `fullAnswer` là câu trả lời mẫu hoàn chỉnh bằng tiếng Việt tự nhiên, ngắn gọn và chuyên nghiệp. Ví dụ kỹ thuật dùng cách dẫn phi cá nhân như “Ví dụ, trong một hệ thống…” thay vì gán trải nghiệm cho ứng viên. Tránh số liệu chính xác/ấn tượng không cần thiết.

Field `sampleAnswer` có thể vắng mặt hoặc `null`; clients phải đọc lịch sử JSON không có field này như evaluation cũ hợp lệ và không có mẫu. Một sample không hợp lệ/không dùng được được bỏ độc lập (null/absent); không retry bằng AI call thứ ba, không đổi token budget hay provider policy, và không làm mất evaluation core hợp lệ.

`strengths` có 1–3 phần tử có bằng chứng khi câu trả lời thể hiện điểm tích cực,
hoặc là collection rỗng khi không có bằng chứng tích cực có thể ground và mọi
điểm rubric đều dưới 60. `improvements` có 1–3 phần tử, mỗi phần tử không rỗng
và tối đa 500 ký tự; `improvedAnswer` không rỗng và tối đa 4.000 ký tự. Improvements phải
actionable. Khi có answer gốc, validator yêu cầu coaching có overlap có ý nghĩa
với answer, giữ nguyên mọi số liệu/công nghệ/thành tích đã có và từ chối facts
mới (bao gồm số chưa xuất hiện trong answer). Khi thiếu bằng chứng, AI phải
khuyến nghị ứng viên bổ sung dữ liệu nếu có thay vì tự tạo dữ liệu. Đây là
coaching trong cùng structured operation, không có rewrite operation riêng.
Whitespace và duplicate tương đương trong `improvements` được normalize trước
khi kiểm tra cardinality. Nếu sau đúng một repair attempt mà chỉ riêng
`improvedAnswer` vẫn ungrounded/fabricated trong khi rubric, evidence, STAR và
các coaching field còn lại đều hợp lệ, server dùng chính candidate answer làm
fallback deterministic (giới hạn theo contract 4.000 ký tự). Core evaluation
không hợp lệ vẫn trả `AI_OUTPUT_INVALID`; fallback không được lấy facts từ câu
hỏi, rubric, JD, CV, Career Goal hay interviewer context.
Nếu repair attempt terminal trả `AI_OUTPUT_INVALID` do structured response không
deserialize được, server chỉ được fallback từ evaluation typed gần nhất khi lần
semantic failure trước đó là riêng `improvedAnswer` ungrounded/fabricated; server
thay đúng field này bằng candidate answer rồi validate lại toàn bộ evaluation.
Nội dung malformed không được parse hoặc dùng làm fallback. Nếu cả hai provider
response đều parse thành typed output nhưng repair attempt terminal có semantic
failure khác, server thử recovery trên output hiện tại trước; nếu không hợp lệ,
server mới thử cặp typed raw/validation ngay trước đó và chỉ khi operation cho
phép recovery theo failure reason trước. Với `improvedAnswer` ungrounded/fabricated,
recovery giữ nguyên toàn bộ evaluation trước đó, chỉ thay `improvedAnswer` bằng
candidate answer rồi chạy lại toàn bộ validation. Không ghép field giữa hai
attempts; rubric/coaching không hợp lệ vẫn fail closed. Raw chưa deserialize thành
công không bao giờ được giữ hoặc dùng làm fallback.

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

Report cũng trả `sample` và `questionReviews` chỉ cho các câu đã có official
answer. `suggestedImprovedAnswers` là projection từ coaching đã persist trong
`answer.evaluation`; report không gọi AI lần hai để rewrite answer. Khi người
dùng kết thúc sớm sau tối thiểu hai câu, `sample.isPartial=true` và `disclaimer`
nêu rõ số câu đã trả lời trên tổng số câu đã phát hành, không coi đó là đánh giá
đủ toàn bộ năng lực. POST `/interviews/{id}/report/retry` yêu cầu
`Idempotency-Key`, owner scope và chỉ tạo thêm một report job khi không có job
đang pending/processing; retry không tạo reservation/consume mới.
GET `/interviews/{id}/report` trả `409 INTERVIEW_REPORT_PROCESSING` khi report
đang chạy, hoặc `409 INTERVIEW_REPORT_FAILED` khi job cuối thất bại và có thể
retry; các trạng thái này không làm interview chuyển sang `failed`.

### Scenario Practice v2

Scenario catalogue data is server-owned and published scenarios are grouped by their active category/track:

```text
GET /api/v1/scenarios/categories
GET /api/v1/scenarios?category={slug}&difficulty={easy|medium|hard}&competency={slug}
GET /api/v1/scenarios/progress
GET /api/v1/scenarios/{id-or-slug}/attempts
POST /api/v1/scenarios/{scenarioId}/retry
```

`POST .../retry` requires `Idempotency-Key` and creates a new draft only after the user's latest attempt for that scenario is terminal (`completed` or `failed`). A draft, queued or processing latest attempt returns `409 SCENARIO_ATTEMPT_IN_PROGRESS`. Attempt history is owner-scoped and returned newest-first. It exposes `overallScore`, `previousScore`, `scoreDelta` and `improved` only for completed attempts with a valid 0–100 evaluation score; it does not expose raw evaluation JSON. The top-level `comparison` contains `currentScore`, `previousScore`, `delta` and `improved` for the latest two usable attempts, or `null` previous values when only one usable attempt exists.

`GET /api/v1/scenarios/progress` aggregates completed scenario scores by category/track, competency and difficulty. Failed or incomplete attempts are counted as attempts but never included in score averages. The server recommends `easy` when there is no completed attempt; after a completed attempt, a score of at least `80` advances one level (`easy → medium → hard`) and a lower score keeps the latest level. This is coaching progress only, not a hiring assessment.

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

Checkout payment responses expose `checkout: { method, url, fields[] }`. SePay uses a signed ordered POST form; the frontend must submit the fields as returned and must not generate signatures. Checkout statuses are `processing`, `pending`, `fulfilled` and terminal `failed`. SePay `ORDER_PAID` requires `CAPTURED` + `APPROVED`; `TRANSACTION_VOID` becomes final unpaid and moves a pending order to `failed`. Duplicate valid IPNs are acknowledged with HTTP 200 and do not create duplicate subscriptions or entitlements. Order `failed` is terminal and refresh does not call the provider again.

## Phân quyền

- Người dùng chỉ truy vấn record có `record.user_id = authenticatedUser.id`.
- Admin actions dùng role riêng và audit log; không dùng email hard-code ở frontend.
- Download file luôn qua signed URL thời hạn ngắn sau khi xác minh owner.
- Payment webhook không dùng bearer user token; chỉ tin payload sau verify signature và event ID chưa từng xử lý.
- Response dùng DTO allow-list thay vì trả trực tiếp EF entity để tránh lộ field nội bộ/role/secret.
