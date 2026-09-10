# Nexora — Implementation Plan trước Production

> **Phạm vi:** Backend production hardening + hoàn thiện product loop cho Nexora  
> **Team Backend:** **Bảo (qb)** và **Bảo Nguyên**  
> **Baseline khi lập kế hoạch:** `main` tại `78fc7f5d719d3a058db59b291a463236e2e5b065` — SignalR resource notifications đã merge.  
> **Nguyên tắc lớn:** chia task theo domain/file ownership để hai người có thể code song song mà hạn chế conflict; REST vẫn là source of truth, SignalR chỉ notification; không rewrite kiến trúc Modular Monolith hiện tại.

---

## 1. Mục tiêu production

Trước khi public production, Nexora cần đạt 5 mục tiêu:

1. **Một vòng trải nghiệm Free đủ thuyết phục**
   - 1 lần phân tích CV miễn phí.
   - 1 buổi mock interview miễn phí.
   - 3 câu nền tảng miễn phí.
   - Sau câu 3, user có thể:
     - nâng cấp để tiếp tục 2–3+ câu chuyên sâu trong **cùng session**; hoặc
     - kết thúc ngay và nhận report từ các câu đã trả lời.
   - Không khóa lại report/history đã tạo chỉ vì gói hết hạn.

2. **CV Analysis có hai mục đích rõ ràng**
   - `job_targeted`: CV + JD cụ thể.
   - `field_benchmark`: CV + ngành/vị trí/cấp độ, không cần JD.

3. **Interview đủ chiều sâu để không còn cảm giác demo**
   - Primary question và follow-up có semantic rõ.
   - 3 câu Free cố định theo mục tiêu đánh giá nền tảng.
   - Paid continuation cá nhân hóa theo CV/JD/CV analysis.
   - Feedback từng câu có: điểm tốt, cần cải thiện, gợi ý câu trả lời tốt hơn.
   - Text input và voice-to-text; transcript được sửa trước khi submit.
   - Không làm camera/body-language trong production scope này.

4. **Production infrastructure đủ an toàn và quan sát được**
   - Cloudflare R2 cho storage production.
   - Resend cho transactional email.
   - Sentry cho error monitoring.
   - UptimeRobot cho uptime monitoring.
   - Health endpoint, SignalR, Neon migration/deployment flow được harden.

5. **Auth đủ tiêu chuẩn public product**
   - Xác minh email.
   - Forgot/reset password.
   - Change password được expose đầy đủ ở FE.
   - Google login là optional nếu tiến độ cho phép.
   - Có thể thêm weekly goal + reminder nếu effort thấp sau khi Resend ổn định.

---

# 2. Priority

## P0 — Bắt buộc trước Production

- Cloudflare R2 production storage.
- Email verification + Resend.
- Forgot/reset password.
- CV Analysis 2 mode.
- Free CV analysis quota = 1.
- Interview contract v1 production.
- Free interview trial = 1 session, 3 câu nền tảng.
- Paid continuation trong cùng session.
- Per-answer coaching: strengths / improvements / improvedAnswer.
- Partial report khi user dừng sau phần Free.
- SignalR integration đầy đủ cho các async state liên quan.
- Sentry API + Worker.
- UptimeRobot production monitor.
- Migration/runbook/backup/recovery/security tests.

## P1 — Nên có ở Production nếu timeline cho phép

- Scenario tracks + retry/attempt comparison.
- STAR Story Bank.
- Skill Profile / Competency Matrix.
- Learning Path cá nhân hóa.
- Next-practice recommendation.
- Progress dashboard nâng cấp.

## P2 — Optional / sau P0

- Google OAuth/login.
- Weekly goal + reminder email/in-app.
- Backend STT provider nếu browser STT không đủ ổn định.
- TTS cho câu hỏi phỏng vấn.
- Advanced scenario branching.
- Advanced trend/comparison analytics.

---

# 3. Phân chia ownership để tránh conflict

## 3.1 Bảo (qb) — Workstream A

**Domain ownership chính:**

- CV / Upload / Document extraction.
- Resume Analysis.
- Interview / Question / Answer / Report.
- AI operation liên quan CV + Interview.
- Free-trial / question-limit semantics của CV + Interview.
- Cloudflare R2.
- Sentry.
- UptimeRobot.
- Shared production wiring/config integration.

**File ownership chính trong thời gian làm song song:**

```text
src/Nexora.Api/Controllers/InterviewsController.cs
src/Nexora.Api/Controllers/ResumesController.cs
src/Nexora.Api/Controllers/ResumeAnalysesController.cs
src/Nexora.Api/Controllers/UploadsController.cs
src/Nexora.Api/Contracts/PracticeContracts.cs

src/Nexora.Business/Practice/PracticeContracts.cs
src/Nexora.Business/Storage/*

src/Nexora.Data/Practice/PracticeService.cs
src/Nexora.Data/Practice/PracticeEntities.cs
src/Nexora.Data/Practice/ResumeContextBuilder.cs

src/Nexora.Integrations/Storage/*

AI operation/validators cho:
- resume.profile
- resume.analysis.*
- interview.*
```

Bảo **không sửa** `ScenarioStarService.cs` trong các phase song song.

---

## 3.2 Bảo Nguyên — Workstream B

**Domain ownership chính:**

- Auth / Identity.
- Resend transactional email.
- Email verification.
- Forgot/reset password.
- Google OAuth optional.
- Scenario.
- STAR Builder / STAR Story Bank.
- Progress / Skill Profile / Learning Path.
- Weekly goals / reminders optional.

**File ownership chính:**

```text
src/Nexora.Api/Controllers/AuthController.cs
src/Nexora.Api/Controllers/MeController.cs
src/Nexora.Api/Contracts/AuthContracts.cs

src/Nexora.Business/Auth/*
src/Nexora.Data/Auth/*
src/Nexora.Data/Identity/*

src/Nexora.Data/Practice/ScenarioStarService.cs
src/Nexora.Business/Practice/PracticeFeatureContracts.cs

src/Nexora.Api/Controllers/ScenariosController.cs
src/Nexora.Api/Controllers/ScenarioAttemptsController.cs
src/Nexora.Api/Controllers/StarAttemptsController.cs
src/Nexora.Api/Controllers/ProgressController.cs

src/Nexora.Integrations/Email/*      (mới)
```

Bảo Nguyên **không sửa** `PracticeService.cs` trong các phase song song.

---

# 4. Hot files — KHÔNG sửa song song

Các file sau dễ conflict nhất. Chỉ **một người được chạm tại một thời điểm**:

```text
src/Nexora.Api/Program.cs
src/Nexora.Integrations/DependencyInjection.cs
src/Nexora.Data/DependencyInjection.cs
src/Nexora.Data/Persistence/NexoraDbContext.cs
src/Nexora.Data/Persistence/Migrations/*
src/Nexora.Data/Persistence/Migrations/NexoraDbContextModelSnapshot.cs
Directory.Packages.props
.env.example
render.yaml
src/Nexora.Api/appsettings*.json
src/Nexora.Worker/appsettings*.json
project_log.md
```

## Quy tắc hot-file

- **Bảo là integration owner** cho `Program.cs`, shared config, production wiring và `Directory.Packages.props` trong P0.
- Mỗi task của Bảo Nguyên cần wiring vào `Program.cs`/DI thì:
  1. implement logic + extension class riêng;
  2. ghi rõ wiring cần thêm trong PR description;
  3. Bảo thực hiện/cherry-pick phần wiring sau khi branch được rebase.
- Không để cả hai cùng chỉnh `Program.cs` hoặc `Integrations/DependencyInjection.cs` trong cùng ngày nếu không cần thiết.

## Quy tắc EF Migration

- Không generate migration trên hai feature branch song song.
- **P0 schema migration cho CV/Interview:** Bảo là migration owner.
- Trong lúc đó Bảo Nguyên ưu tiên auth feature không cần schema mới (ASP.NET Identity đã có EmailConfirmed, token providers và external login tables).
- Sau khi P0 CV/Interview merge xong, **P1 Learning/STAR schema:** Bảo Nguyên là migration owner.
- Người không phải migration owner **không commit ModelSnapshot**.

---

# 5. Git workflow

Mỗi task lớn là một branch + PR nhỏ, không gom tất cả vào một mega PR.

Ví dụ:

```text
Bảo:
feat/r2-storage-provider
feat/resume-analysis-modes
feat/interview-contract-v1
feat/interview-free-continuation
feat/interview-answer-coaching
chore/production-observability

Bảo Nguyên:
feat/email-verification
feat/password-recovery
feat/resend-email-provider
feat/scenario-practice-v2
feat/star-story-bank
feat/learning-path
feat/weekly-goals-reminders
feat/google-auth                  # optional
```

### Rule

- Branch mới luôn tạo từ `main` mới nhất.
- Trước khi mở PR: rebase/merge `main` mới nhất vào branch.
- Mỗi PR chỉ có **một mục tiêu**.
- Không merge 2 PR cùng sửa migration snapshot.
- Merge xong một hot-file PR thì người còn lại sync `main` ngay.
- Trước khi bắt đầu một task song song, đọc entry mới nhất trong `project_log.md` và kiểm tra branch/PR/commit liên quan. Nếu task phụ thuộc phần của teammate, chỉ bắt đầu sau khi log xác nhận phần đó đã hoàn tất và đã merge hoặc có commit rõ ràng để sync.
- Sau khi hoàn tất task, cập nhật `project_log.md` trong cùng PR với ngày, branch, commit/PR, phạm vi file hoặc contract, test đã chạy và dependency hoặc blocker còn lại. Đây là sổ đồng bộ chính để teammate biết phần nào đã sẵn sàng.
- Nếu `project_log.md` chưa xác nhận dependency đã xong, không tự đoán trạng thái và không sửa các file phụ thuộc; hỏi hoặc đồng bộ với owner trước.
- Mỗi PR phải có:
  - changed contracts;
  - migration impact;
  - FE impact;
  - new env/config keys;
  - tests run;
  - rollback note nếu có.

### Quy trình đồng bộ khi làm song song

1. Đọc `project_log.md`, kiểm tra `main` và các branch liên quan trước khi code.
2. Xác nhận dependency của task đã có entry hoàn tất; nếu chưa có thì chờ owner cập nhật hoặc thống nhất thứ tự thực hiện.
3. Làm trên branch riêng, tránh hot-file đang được owner khác giữ.
4. Khi xong, ghi log và mở PR; teammate chỉ lấy phần phụ thuộc sau khi đã sync commit hoặc merge mới nhất.

---

# 6. PHASE 0 — Contract freeze & conflict reduction

**Thời lượng mục tiêu:** 1–2 ngày.  
**Mục tiêu:** chốt semantics trước khi hai người code sâu.

## P0.1 — Chốt product rules [CẢ HAI]

Chốt bằng doc/ADR, chưa code AI lớn.

### Free plan

```text
CV Analysis:
- 1 lần miễn phí / account.
- User được chọn 1 trong 2 mode.

Interview:
- 1 free trial session / account.
- Free question limit = 3.
- Sau Q3:
  - finish now -> report với 3 answer; hoặc
  - upgrade -> continue cùng session.
```

### Paid question limit

Không hard-code. Dùng plan/feature policy:

```text
Free    -> 3 câu / session
Basic   -> đề xuất 5–6 câu / session
Pro     -> đề xuất 8–10 câu / session
```

**Session quota** và **questions/session** là hai thứ khác nhau.

## P0.2 — Chốt interview question semantics [BẢO]

Primary questions free mặc định:

1. `self_introduction`
   - giới thiệu bản thân / kinh nghiệm / định hướng.
2. `behavioral_star`
   - một thử thách, vấn đề hoặc conflict và cách xử lý.
3. `motivation_role_fit`
   - lý do quan tâm vị trí + điểm phù hợp.

Paid continuation:

- `technical`
- `cv_targeted`
- `jd_targeted`
- `scenario`
- `behavioral`
- adaptive `followup`

Question cần semantic rõ:

```text
Question
- id
- sequence
- kind: primary | followup
- topic
- parentQuestionId?      # follow-up
- storyGroupId?          # behavioral/STAR aggregation nếu cần
```

Không còn dùng assumption `Sequence > 1 = follow-up`.

## P0.3 — Chốt CV analysis contract [BẢO]

```text
mode = job_targeted
require: resumeId + jobDescriptionId

mode = field_benchmark
require: resumeId + industry + targetRole + seniority
jobDescriptionId: null
```

## P0.4 — Chốt Auth flow [BẢO NGUYÊN]

```text
Register
-> account created, EmailConfirmed=false
-> send verification email
-> không cho dùng AI quota trước khi verify

Verify email
-> EmailConfirmed=true
-> provision/ensure Free entitlement idempotently
-> cho phép login / tạo session

Forgot password
-> luôn trả response generic
-> send token nếu account hợp lệ

Reset password
-> validate Identity token
-> reset password
-> revoke old sessions/security stamp
```

> **Lưu ý hiện trạng:** Backend đã có `POST /api/v1/me/password` và logic revoke session sau đổi mật khẩu. Không re-implement từ đầu; chỉ kiểm tra tests + nối FE. Phần còn thiếu chính là verify email + forgot/reset.

---

# 7. PHASE 1 — Production foundation (code song song)

**Thời lượng mục tiêu:** 3–5 ngày.

## Workstream A — Bảo

### A1. Cloudflare R2 provider [P0]

**Mục tiêu:** bỏ phụ thuộc local disk production.

Tasks:

- [x] Thêm `Storage:Provider = local | r2`, unknown value fail-closed.
- [x] Implement `R2StorageProvider : IStorageProvider`.
- [x] S3-compatible Cloudflare R2 config:
  - account endpoint;
  - bucket;
  - access key;
  - secret key. Public/custom domains are intentionally not part of A1; objects remain private.
- [x] Logical object keys accept the target `resumes/{userId}/...`, `avatars/{userId}/...` and reserved `audio/{userId}/...` prefixes; the existing `IStorageProvider.SaveAsync` key format remains unchanged for backward compatibility until a user-scoped key contract is approved. Audio remains reserved and is not stored by default.
- [x] `OpenReadAsync` phải dùng được từ Worker để extract CV.
- [x] `DeleteAsync` phải được privacy/deletion flow gọi được.
- [x] Không log R2 credentials/presigned URL có signature.

### A2. Production upload intent [P0]

Current local upload intent ở memory không phù hợp production multi-process/restart.

Preferred production flow:

```text
FE -> POST upload intent
BE -> signed R2 PUT URL
FE -> PUT trực tiếp R2
BE -> finalize/confirm upload
Worker -> open object -> validate magic bytes/MIME -> extract
```

Tasks:

- [x] Không tin extension/content-type do FE gửi: R2 finalize đọc object thật qua `IStorageProvider` và áp dụng validator signature/container.
- [x] Validate size ở request, object metadata và stream thực tế; giới hạn mặc định 10 MiB.
- [x] Validate PDF/DOCX signature/container sau upload trước khi resume thành usable; checksum được lưu để Worker kiểm tra integrity lần nữa.
- [x] Upload intent có expiry và expiry được kiểm tra cả trước khi đọc object lẫn trong transaction finalize.
- [x] Không dùng in-memory dictionary làm source of truth production: intent state nằm trong bảng `upload_intents`, token chỉ lưu dưới dạng SHA-256 hash.
- [x] Local provider vẫn chạy Development/Testing; Production + `Storage:Provider=local` tiếp tục fail-closed.
- [x] Durable upload intent token hash; finalize/replay/concurrent `POST /resumes` không tạo duplicate resume, stored file hoặc outbox. Presign creation itself is not idempotent-key based.
- [x] R2 upload URL là signed HTTPS PUT ngắn hạn cho private object; không ghi URL/token/credentials vào log hoặc database.

### A3. R2 tests

- [x] Unit test provider mapping/config validation.
- [x] Test object key không cho path traversal.
- [x] Test delete/open behavior.
- [x] Test signed-upload expiry/security, owner isolation, actual object validation, idempotent replay và persistent intent state.
- [x] Không dùng live paid R2 trong unit/integration gate.

---

## Workstream B — Bảo Nguyên

### B1. Email abstraction + Resend [P0]

Tạo provider-neutral abstraction trong Business:

```text
IEmailSender
- SendVerificationAsync(...)
- SendPasswordResetAsync(...)
- SendReminderAsync(...)   # có thể để generic template/send method
```

Hoặc generic `SendAsync(EmailMessage)` nếu muốn giảm coupling.

Tasks:

- [x] `ResendEmailSender` trong `Nexora.Integrations/Email`.
- [x] Development/testing có fake/no-op/recording sender.
- [x] Config validation fail-closed production nếu Resend enabled nhưng thiếu key/domain.
- [x] Không log token verification/reset.
- [x] Template tiếng Việt responsive tối thiểu.
- [x] From domain được cấu hình, không hard-code.

### B2. Email verification [P0]

- [x] Register tạo account chưa verified.
- [x] Generate verification token bằng ASP.NET Identity token provider.
- [x] Link verification chỉ chứa one-time token + user identifier cần thiết.
- [x] TTL hợp lý.
- [x] `POST /auth/verify-email` hoặc GET callback contract rõ ràng.
- [x] `POST /auth/resend-verification` rate limit.
- [x] Login account chưa verify trả `EMAIL_NOT_VERIFIED`.
- [x] Free entitlement provisioning phải idempotent và chỉ usable sau verify.
- [x] Không cho spam tạo nhiều entitlement bằng retry verification.

### B3. Password recovery [P0]

- [x] `POST /auth/forgot-password`.
- [x] Response generic dù email tồn tại hay không.
- [x] Rate limit theo IP/email normalized.
- [x] Gửi reset link qua Resend.
- [x] `POST /auth/reset-password`.
- [x] Reset thành công revoke refresh tokens + update security stamp.
- [x] Token invalid/expired trả error an toàn, không leak account existence.

### B4. Change password existing [P0]

Backend đã có logic đổi mật khẩu.

- [x] Verify contract + regression tests.
- [x] Backend contract documented for FE: `POST /api/v1/me/password`.
- [ ] UX yêu cầu current password + new password + confirm.
- [ ] Sau success FE logout/re-bootstrap vì BE revoke sessions.

---

# 8. PHASE 2 — Core Product Production (code song song)

**Thời lượng mục tiêu:** 7–10 ngày.

# Workstream A — Bảo: CV + Interview

## A4. CV Analysis v2 — hai mode [P0]

### Mode 1 — `job_targeted`

Input:

```text
ResumeProfile
+ JobDescription
```

Output tối thiểu:

```text
matchScore
summary
matchedKeywordsOrSkills[]
missingKeywordsOrSkills[]
strengths[]
gaps[]
recommendations[]
sectionFeedback[]
breakdown:
  technicalSkillMatch
  experienceRelevance
  impactEvidence
  clarity
  structure
```

### Mode 2 — `field_benchmark`

Input:

```text
ResumeProfile
+ industry
+ targetRole
+ seniority
```

Output:

```text
readinessScore
summary
strengths[]
gaps[]
recommendations[]
sectionFeedback[]
breakdown:
  technicalFoundation
  projectEvidence
  experiencePresentation
  impactAchievements
  clarity
  roleAlignment
```

Tasks:

- [x] `ResumeAnalysisMode` explicit, không suy ra bằng `jobDescriptionId == null`.
- [x] Reuse cached `ResumeProfile`; không extract/profile lại cho mỗi mode.
- [x] Structured AI schema + validators server-authoritative.
- [x] Không làm yếu strict validator hiện tại.
- [x] History lưu context/mode/model/prompt/schema version.
- [ ] Có thể compare các lần analysis về sau.

## A5. Free CV analysis quota [P0]

- [x] Free entitlement: 1 shared analysis/account across both analysis modes.
- [x] User dùng lượt cho mode nào cũng được; `job_targeted` và `field_benchmark` dùng cùng `cv_analysis` feature.
- [x] Failure trước usable result phải void/refund reservation theo semantics tương ứng.
- [x] Retry idempotent không trừ thêm lượt; concurrent requests không vượt quota.
- [x] Paid limits configurable theo plan, không hard-code controller.

## A6. Interview Contract v1 production [P0]

### Question model

- [ ] `primary` vs `followup`.
- [ ] `ParentQuestionId?`.
- [ ] topic/kind rõ.
- [ ] migration do **Bảo** tạo trong phase này.
- [ ] sửa STAR story aggregation để group đúng story, không còn assumption tất cả seq>1 cùng Q1.
- [ ] regression test giữ canonical STAR weights 20/20/35/25.

### Interview plan

```text
Free trial:
Q1 self introduction
Q2 behavioral / STAR
Q3 motivation / role fit

Paid continuation:
Q4+ CV/JD/technical/adaptive
follow-up chỉ xuất hiện khi evaluation thực sự cần đào sâu
```

- [ ] Không pre-generate paid question trước khi entitlement cho phép.
- [ ] Không tốn AI call cho câu bị paywall.

## A7. Free interview + paid continuation [P0]

Tách rõ:

```text
interview session quota
!=
question limit per session
```

Đề xuất policy:

```text
Free  : 1 trial session, maxQuestions=3
Basic : session quota theo plan, maxQuestions=5 hoặc 6
Pro   : session quota theo plan, maxQuestions=8–10
```

Sau Q3 Free:

```text
nextQuestion = null
isComplete = false
continuation.state = upgrade_required
continuation.canFinishNow = true
continuation.canUpgradeAndContinue = true
```

User chọn **Finish now**:

```text
active -> completing -> completed
report dùng đúng 2–3 answers hiện có
```

User upgrade:

```text
webhook verified
-> entitlement updated
-> same interview session remains valid
-> POST continue / generate-next idempotently
-> generate Q4
```

Tasks:

- [ ] Không tạo session mới sau upgrade.
- [ ] Re-check entitlement server-side.
- [ ] Endpoint continue/generate-next có Idempotency-Key.
- [ ] Không tin `paid=true` từ FE.
- [ ] Session state machine không thêm `paywalled` nếu không cần; paywall là continuation state/policy.

## A8. Per-answer coaching [P0]

Mở rộng `interview.evaluate` trong **cùng AI call**:

```text
scores
strengths[]
improvements[]
improvedAnswer
feedback
star
scoreScale
```

Rules:

- [ ] `strengths` grounded vào answer thật.
- [ ] `improvements` cụ thể/actionable.
- [ ] `improvedAnswer` không được tự bịa metric/thành tích/công nghệ mà user chưa cung cấp.
- [ ] Có thể dùng placeholder coaching khi thiếu evidence: “hãy bổ sung số liệu cụ thể nếu có”.
- [ ] Không thêm AI call thứ 2 chỉ để rewrite answer.
- [ ] Structured validators kiểm tra nonblank, max length, grounded constraints hợp lý.

## A9. Interview report production [P0]

Report cần hỗ trợ cả session ngắn và dài:

- [ ] Overall score.
- [ ] Rubric breakdown.
- [ ] STAR summary nếu applicable.
- [ ] Strengths.
- [ ] Gaps.
- [ ] Action plan.
- [ ] Per-question review.
- [ ] Suggested improved answers.
- [ ] Report 2–3 câu vẫn hợp lệ, không giả vờ đã đánh giá full competency.
- [ ] Có disclaimer về sample size nếu report từ free/partial session.

## A10. Voice input contract [P1]

Không camera.

Flow:

```text
voice -> STT -> editable transcript -> user confirm -> submit text
```

P0 có thể để FE/browser speech xử lý nếu ổn.

Backend:

- [ ] Canonical answer luôn là **text user đã xác nhận**.
- [ ] Không chấm raw transcript nếu user đã edit.
- [ ] Không lưu audio mặc định.
- [ ] Nếu sau này thêm STT backend: tạo `ISpeechToTextProvider`, không coupling interview service vào provider cụ thể.
- [ ] Audio upload nếu có phải opt-in + retention rõ.

---

# Workstream B — Bảo Nguyên: Auth hoàn thiện + Practice learning foundation

## B5. Auth regression/hardening [P0]

- [x] Register/verify/login/refresh/logout happy path.
- [x] Unverified account không gọi AI.
- [x] Verification resend rate-limit.
- [x] Password reset revokes sessions.
- [x] Deleted/inactive user fail closed.
- [x] Security stamp vẫn tương thích SignalR JWT validation.
- [x] Refresh cookie cross-site production config test.

## B6. Scenario async SignalR parity [P1]

Current SignalR layer đã có resource notifications; bổ sung scenario/star nếu FE còn poll nhanh.

- [x] Persist notification khi Scenario/STAR attempt transition thành `completed` hoặc `failed`.
- [x] SignalR/WebSocket integration test refetch REST resource và giữ đúng owner/payload contract.

Emit khi persisted state thực sự đổi:

```text
scenarioAttempt.completed
scenarioAttempt.failed
starAttempt.completed
starAttempt.failed
```

Không invent status khác DB.

FE nhận notification -> refetch REST một lần.

## B7. Scenario Practice v2 [P1]

Không sửa Interview files.

- [x] Track/category grouping qua public category catalogue, category filter và track progress aggregate.
- [x] Difficulty progression theo score completed gần nhất (`80+` tăng một level, thấp hơn giữ level hiện tại).
- [x] Retry cùng scenario qua endpoint idempotent, chặn retry khi attempt trước còn active.
- [x] Attempt history theo từng scenario, owner-scoped.
- [x] Compare score attempt gần nhất bằng `previousScore` và `scoreDelta`.
- [x] Competency aggregate trong scenario progress.
- [ ] Saved/bookmark (optional): deferred vì cần schema/migration user-scenario riêng; không thêm migration ngoài ownership của workstream.

Không cần branching scenario ở P1 nếu timeline căng.

## B8. STAR Story Bank [P1]

Tách khỏi one-off STAR attempt:

```text
StarStory
- id
- userId
- title
- competencies/tags
- situation
- task
- action
- result
- latestScore
- createdAt
- updatedAt
```

Tasks:

- [ ] Convert/save từ STAR attempt thành story.
- [ ] Edit story.
- [ ] Re-evaluate sau edit theo quota/policy.
- [ ] List/search story bank.
- [ ] Tags: leadership, ownership, conflict, teamwork, problem-solving...
- [ ] Không tự expose story nội dung cho interviewer trước khi user trả lời.

---

# 9. PHASE 3 — Learning loop + Observability

**Thời lượng mục tiêu:** 5–7 ngày.

## Workstream A — Bảo: Observability / Production infra

## A11. Sentry [P0]

API + Worker đều phải có Sentry.

Tasks:

- [ ] Add Sentry ASP.NET Core integration.
- [ ] Worker exception capture.
- [ ] Environment: Development/Staging/Production.
- [ ] Release/version tag từ commit SHA/deploy version.
- [ ] Correlation/request ID attach vào event.
- [ ] Background job aggregate ID có thể attach dạng safe tag.
- [ ] `SendDefaultPii = false`.
- [ ] Không gửi:
  - JWT/Authorization;
  - refresh token;
  - DeepSeek/Gemini key;
  - raw CV;
  - answer/transcript;
  - AI prompt/response;
  - payment secret.
- [ ] Filter expected 4xx/business validation để tránh noise.
- [ ] Alert cho unhandled 5xx/job failure spike.

## A12. UptimeRobot [P0]

Không cần SDK trong repo.

Production monitor:

```text
GET https://<api-domain>/health/live
```

- [ ] 5 phút/lần hoặc mức Free plan hỗ trợ.
- [ ] Alert email tới team.
- [ ] Không dùng endpoint trả nội dung nhạy cảm.
- [ ] Nếu readiness endpoint public-safe, có thể thêm monitor thứ 2 sau.
- [ ] Runbook ghi rõ: uptime alert -> check Sentry -> check Render/Railway -> check Neon.

## A13. Production config hardening [P0]

- [ ] `.env.example` có tên key nhưng không secret.
- [ ] Production Safety validate R2/Resend/Sentry config cần thiết.
- [ ] Render/Railway env docs.
- [ ] `Frontend:AllowedOrigins` đúng Vercel custom domain.
- [ ] SignalR hub CORS/JWT vẫn pass.
- [ ] Rotate các secret đã từng xuất hiện trong screenshot/chat nếu còn dùng.

---

## Workstream B — Bảo Nguyên: Skill Profile + Learning Path

## B9. Career Goal / Target Role [P1]

Đây là glue cho CV, Interview, Learning Path.

```text
CareerGoal
- targetRole
- seniority
- industry?
- targetCompany?
- targetJobDescriptionId?
- targetDate?
- active
```

Chỉ tạo schema sau khi Bảo merge P0 CV/Interview migration.

## B10. Skill Profile [P1]

Không để AI “đoán score” tự do.

Aggregate từ evidence thật:

```text
CV analysis gaps
Interview rubric
STAR components
Scenario competencies
```

Ví dụ:

```text
Technical
- API Design
- SQL
- Caching
- Security

Interview
- Correctness
- Structure
- Completeness
- Clarity

Behavioral
- Situation
- Task
- Action
- Result
```

- [ ] Score có source/evidence count.
- [ ] Không update competency nếu chưa đủ signal.
- [ ] Có timestamp/latest evidence.

## B11. Learning Path [P1]

Learning path không chỉ là một đoạn text AI.

```text
LearningPath
- careerGoalId
- status
- milestones[]
- activities[]
- progress
```

Activity có thể reference:

```text
scenario
star drill
interview
resume improvement
external learning item (optional text/link)
```

- [ ] Generate từ gaps hiện tại.
- [ ] Recalculate/adapt sau practice mới.
- [ ] Không regenerate toàn path sau mỗi event nếu không cần.
- [ ] Preserve completed activities.

## B12. Next Practice Recommendation [P1]

Endpoint gợi ý một hành động tiếp theo:

```text
GET /api/v1/recommendations/next
```

Output:

```text
reason
activityType
resourceId?
estimatedMinutes
priority
```

Rule-based trước, AI optional sau.

Ưu tiên weakness có evidence mạnh + chưa luyện gần đây.

## B13. Progress Dashboard v2 [P1]

Nâng từ analytics cơ bản thành readiness dashboard:

- [ ] Career readiness summary.
- [ ] Weakest competencies.
- [ ] Recent improvements.
- [ ] Weekly completed activities.
- [ ] Next recommended practice.
- [ ] Giữ existing historical stats để không break FE cũ hoặc version contract rõ.

---

# 10. PHASE 4 — Optional product polish

**Chỉ bắt đầu khi P0 xanh.**

## Bảo Nguyên — Weekly Goals + Reminders [P2]

### B14. Weekly Goal

User chọn hoặc system đề xuất:

```text
2 interviews / tuần
3 scenarios / tuần
2 STAR drills / tuần
```

Không cần gamification phức tạp.

### B15. Reminder preference

```text
remindersEnabled
emailEnabled
preferredDay/time
timezone
```

Default **opt-out / không gửi mail khi chưa consent**.

### B16. Reminder Worker

Dùng Resend đã có.

Flow:

```text
Worker chạy định kỳ
-> tìm user opt-in chưa đạt goal
-> kiểm tra cooldown/idempotency
-> send email reminder
-> persist sent key
```

Idempotency key đề xuất:

```text
userId + reminderType + ISO-week
```

Không gửi nhiều email cùng tuần nếu không có product rule rõ.

Email reminder ví dụ:

```text
“Tuần này bạn đã hoàn thành 1/2 buổi phỏng vấn.
Bạn còn 1 buổi để đạt mục tiêu tuần.”
```

Nếu effort cao: chỉ ship **in-app weekly goal** trước, email reminder để sau.

---

## Bảo Nguyên — Google OAuth [P2 optional]

Chỉ làm sau email/password auth ổn định.

- [ ] FE lấy Google auth result/code.
- [ ] Backend verify Google server-side.
- [ ] Chỉ tin email khi Google xác nhận `email_verified`.
- [ ] Create/link local Identity user idempotently.
- [ ] Provision Free entitlement đúng một lần.
- [ ] Không tạo duplicate account theo casing email.
- [ ] Có policy rõ cho account đã tồn tại bằng password.
- [ ] Google login cuối cùng vẫn phát Nexora access/refresh session như login thường.

Không để Google token trở thành token authorize trực tiếp Nexora API.

---

# 11. PHASE 5 — Integration & Production Release Gate

**Thời lượng mục tiêu:** 4–5 ngày.

Trong phase này **feature freeze**. Chỉ bugfix/hardening.

## 11.1 Full API contract check

- [ ] Swagger/OpenAPI không có accidental breaking changes chưa documented.
- [ ] FE contract cho auth verify/reset.
- [ ] FE contract CV 2 mode.
- [ ] FE contract interview continuation/paywall.
- [ ] FE contract SignalR resourceChanged.
- [ ] FE report không poll 3s liên tục khi SignalR available; fallback 15–30s tối đa.

## 11.2 Database

- [ ] Tất cả migrations apply clean trên fresh DB.
- [ ] Upgrade từ current staging Neon schema chạy clean.
- [ ] `dotnet ef migrations has-pending-model-changes` = false.
- [ ] Backup trước production migration.
- [ ] Restore drill thành công.
- [ ] Migration order documented.

## 11.3 Security

- [ ] User A không đọc CV/interview/report/story/goal của user B.
- [ ] Email verification token không leak logs.
- [ ] Forgot password không enumerate account.
- [ ] Refresh cookie production flags đúng.
- [ ] R2 object private by default.
- [ ] Signed upload expire đúng.
- [ ] Delete account xoá/queue xoá R2 object liên quan.
- [ ] Rate limit auth/email endpoints.
- [ ] Sentry PII scrubbing test.

## 11.4 Billing/Freemium

Test end-to-end:

### Free CV

```text
register -> verify -> Free entitlement
-> analysis #1 success
-> analysis #2 blocked trước khi AI call
```

### Free interview

```text
start trial
-> Q1
-> Q2
-> Q3
-> upgrade_required
```

Option A:

```text
Finish now
-> partial report generated
```

Option B:

```text
Checkout
-> verified webhook
-> entitlement upgraded
-> continue SAME session
-> Q4+
```

- [ ] Retry không double-consume quota.
- [ ] Failure trước first useful question không mất trial.
- [ ] Report retry vẫn free/idempotent.

## 11.5 Observability

- [ ] UptimeRobot nhận `/health/live` 200.
- [ ] Test intentionally captured exception xuất hiện ở Sentry staging.
- [ ] Sentry event không có Authorization/CV/answer/secret.
- [ ] Worker failure có event + correlation safe.
- [ ] Alert test được gửi tới team.

## 11.6 Performance

- [ ] Non-AI API P95 target < 500ms trong workload hợp lý.
- [ ] Không relay file lớn qua API nếu R2 presigned flow đã ship.
- [ ] Resume extraction memory không spike quá mức với file 10MB.
- [ ] Interview answer evaluation không tạo duplicate request do FE double click.
- [ ] SignalR reconnect không gây duplicate business operation.

---

# 12. Task board đề xuất

## BẢO — Recommended order

| ID | Task | Priority | Dependency | Conflict risk |
|---|---|---|---|---|
| A1 | R2StorageProvider | P0 | none | Low |
| A2 | Production upload/presign (delivered locally; review pending) | P0 | A1 | Medium |
| A4 | CV Analysis 2 modes | P0 | contract freeze | Low |
| A5 | Free CV analysis entitlement | P0 | A4 | Medium |
| A6 | Interview question model/parent relation | P0 | contract freeze | High DB — Bảo owns migration |
| A7 | Free Q1–Q3 + paid continuation | P0 | A6 | Medium |
| A8 | strengths/improvements/improvedAnswer | P0 | A6 | Medium AI catalog |
| A9 | Partial/full report | P0 | A7/A8 | Medium |
| A11 | Sentry API + Worker | P0 | core merge stable | Medium shared wiring |
| A12 | UptimeRobot | P0 | prod URL | Very low |
| A13 | Production config/runbook | P0 | A1/A11/B1 | Medium |
| A10 | Voice input/STT contract | P1 | interview stable | Low |

## BẢO NGUYÊN — Recommended order

| ID | Task | Priority | Dependency | Conflict risk |
|---|---|---|---|---|
| B1 | IEmailSender + Resend | P0 | none | Low except DI wiring |
| B2 | Email verification | P0 | B1 | Low |
| B3 | Forgot/reset password | P0 | B1 | Low |
| B4 | Change password FE/contract verification | P0 | none | Low |
| B5 | Auth regression/hardening | P0 | B2/B3 | Low |
| B6 | Scenario/STAR SignalR parity | P1 | SignalR baseline | Low |
| B7 | Scenario v2 | P1 | B5 stable | Low |
| B8 | STAR Story Bank | P1 | P0 migration merged | Medium DB — Bảo Nguyên owns migration |
| B9 | Career Goal | P1 | P0 migration merged | Medium DB |
| B10 | Skill Profile | P1 | B7/B8/B9 | Medium |
| B11 | Learning Path | P1 | B10 | Medium |
| B12 | Next Practice | P1 | B10 | Low |
| B13 | Progress Dashboard v2 | P1 | B10/B12 | Low |
| B14–16 | Weekly goal/reminder | P2 | Resend + B11 | Low/Medium |
| B17 | Google OAuth | P2 | B5 | Medium auth |

---

# 13. Parallel execution map

## Sprint/Phase A

```text
Bảo                         Bảo Nguyên
────────────────────        ────────────────────
R2 provider                 Resend provider
R2 upload flow              Email verification
CV analysis contract        Forgot/reset password
```

Ít đụng file nhau.

## Sprint/Phase B

```text
Bảo                         Bảo Nguyên
────────────────────        ────────────────────
CV Analysis v2              Auth hardening/tests
Free CV quota               Scenario/STAR SignalR parity
Interview DB/model          Scenario v2 (không migration nếu có thể)
```

**Bảo là migration owner.**

## Sprint/Phase C

```text
Bảo                         Bảo Nguyên
────────────────────        ────────────────────
Interview continuation      STAR Story Bank
Answer coaching             Career Goal
Report                      Skill Profile
```

Bảo merge Interview schema trước. Sau đó **Bảo Nguyên mới generate P1 migration**.

## Sprint/Phase D

```text
Bảo                         Bảo Nguyên
────────────────────        ────────────────────
Sentry                      Learning Path
UptimeRobot                 Next Recommendation
Prod config/runbook         Progress v2
                            Weekly goal optional
```

## Final

```text
Cả hai:
- feature freeze
- integration tests
- staging soak
- migration rehearsal
- security review
- production deploy checklist
```

---

# 14. API/contract mới dự kiến

Tên chính xác có thể thay đổi nhưng semantics nên giữ.

## Auth

```text
POST /api/v1/auth/register
POST /api/v1/auth/verify-email
POST /api/v1/auth/resend-verification
POST /api/v1/auth/forgot-password
POST /api/v1/auth/reset-password
POST /api/v1/auth/google                 # optional
POST /api/v1/me/password                 # đã có
```

## CV

```text
POST /api/v1/resume-analyses
GET  /api/v1/resume-analyses/{id}
GET  /api/v1/resume-analyses
```

Create request có `mode` rõ.

## Interview

```text
POST /api/v1/interviews
POST /api/v1/interviews/{id}/answers
POST /api/v1/interviews/{id}/continue     # hoặc semantic tương đương
POST /api/v1/interviews/{id}/complete
GET  /api/v1/interviews/{id}
GET  /api/v1/interviews/{id}/report
```

Answer response nên expose continuation state thay vì FE đoán bằng question count.

## Learning P1

```text
GET/POST/PATCH /api/v1/career-goals
GET            /api/v1/skill-profile
GET            /api/v1/learning-path
GET            /api/v1/recommendations/next
GET            /api/v1/progress
```

---

# 15. Configuration dự kiến

Không commit secret thật.

```text
Storage__Provider=r2
Storage__R2__AccountId=
Storage__R2__Bucket=
Storage__R2__AccessKeyId=
Storage__R2__SecretAccessKey=
Storage__R2__Endpoint=

Email__Provider=resend
Email__Resend__ApiKey=
Email__FromAddress=
Email__FromName=Nexora
Frontend__PublicUrl=https://...

Sentry__Dsn=
Sentry__Environment=production

Authentication__Google__ClientId=        # optional
```

UptimeRobot config nằm ngoài repo; runbook ghi monitor URL + contact.

---

# 16. Definition of Done cho mọi task

Mỗi task chỉ Done khi:

- [ ] Build 0 error.
- [ ] Tests liên quan pass.
- [ ] Không làm fail full unit/integration suite.
- [ ] `dotnet format --verify-no-changes` pass.
- [ ] `git diff --check` pass.
- [ ] Không có secret trong diff/log/test fixture.
- [ ] API contract được document nếu thay đổi.
- [ ] FE impact ghi trong PR.
- [ ] Migration impact ghi trong PR.
- [ ] Không có paid live AI call trong automated tests.
- [ ] AI validator/reasoning retry invariants hiện tại không bị weaken.
- [ ] User ownership/authorization có test nếu thêm resource mới.

---

# 17. Những thứ KHÔNG nên làm trước Production P0

Để tránh scope creep:

- Không làm camera/body-language analysis.
- Không làm mentor marketplace.
- Không bán khóa học.
- Không microservice hóa.
- Không Kubernetes.
- Không Redis/backplane SignalR trừ khi thật sự scale >1 API instance trước launch.
- Không branching scenario phức tạp trước khi scenario cơ bản + learning loop ổn.
- Không lưu audio/video mặc định.
- Không dùng AI để tính mọi recommendation nếu rule-based đủ tốt.

---

# 18. Go-live checklist ngắn

## Product

- [ ] 1 free CV analysis hoạt động.
- [ ] 1 free interview trial hoạt động.
- [ ] 3 câu free đúng template.
- [ ] Q3 -> finish hoặc upgrade/continue.
- [ ] Partial report hợp lệ.
- [ ] Paid continuation cùng session.
- [ ] CV analysis 2 mode.
- [ ] Per-answer coaching đầy đủ.

## Auth

- [ ] Verify email.
- [ ] Resend verification.
- [ ] Forgot/reset password.
- [ ] Change password FE.
- [ ] Session revoke đúng.

## Infrastructure

- [ ] R2 production.
- [ ] Neon migrated + backup.
- [ ] Resend verified domain.
- [ ] Sentry alert.
- [ ] UptimeRobot alert.
- [ ] SignalR production connection.

## Security

- [ ] Ownership tests.
- [ ] R2 private.
- [ ] No secret leak.
- [ ] Rate limits.
- [ ] CORS production domain đúng.
- [ ] Email/account enumeration protected.

## Operations

- [ ] Production runbook cập nhật.
- [ ] Rollback path.
- [ ] DB restore test.
- [ ] Staging soak 24–48h nếu timeline cho phép.
- [ ] Team biết xem Sentry/UptimeRobot/hosting logs.

---

# 19. Thứ tự merge khuyến nghị

Để giảm conflict tối đa:

```text
1. B1 Resend abstraction/provider
2. A1 R2 provider
3. B2 Email verification
4. B3 Password recovery
5. A2 Production R2 upload (delivered locally; review pending)
6. A4 CV Analysis modes (after A2 merge)
7. A5 Free CV quota
8. A6 Interview question model + migration
9. A7 Free/paywall continuation
10. A8 Per-answer coaching
11. A9 Report production
12. B5 Auth hardening
13. B6 Scenario/STAR realtime parity
14. B7 Scenario v2
15. B8/B9 STAR Story + Career Goal + Bảo Nguyên migration
16. B10–B13 Skill/Learning/Progress
17. A11 Sentry
18. A12 UptimeRobot
19. A13 Production config/runbook
20. B14–B17 Optional reminders/Google OAuth
21. Feature freeze + release gate
```

> Không bắt buộc merge đúng từng số nếu một task bị block, nhưng **không được phá migration ownership và hot-file ownership**.

---

# 20. Kết luận

Với team 2 backend dev, cách an toàn nhất là:

```text
Bảo
= CV + Interview + R2 + Observability + shared production wiring

Bảo Nguyên
= Auth + Resend + Scenario/STAR + Learning loop + optional reminders/Google
```

Hai workstream chỉ gặp nhau ở ba điểm cần kiểm soát:

1. **shared DI/Program/config** -> Bảo làm integration owner;
2. **EF migrations** -> một migration owner theo phase;
3. **product contracts** -> freeze semantics trước khi code.

Nếu giữ nguyên boundary này, phần lớn thời gian hai người có thể code song song mà không cùng sửa `PracticeService.cs`, `ScenarioStarService.cs`, `AuthController.cs` hay storage/auth integration của nhau.
