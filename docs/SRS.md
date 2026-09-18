# Software Requirements Specification (SRS) — Nexora

| Thuộc tính | Giá trị |
| --- | --- |
| Phiên bản | 1.3 active-interview resume-context corrective amendment |
| Ngày | 18/09/2026 |
| Trạng thái | Approved implementation baseline; DEC-01–04 vẫn deferred trước production enablement, không biểu thị external stakeholder/legal approval |
| Chuẩn tham chiếu | Cấu trúc yêu cầu dựa trên tinh thần của [ISO/IEC/IEEE 29148:2018](https://www.iso.org/standard/72089.html) |
| Phạm vi release | Nexora MVP production — mock interview và career-preparation, không phải live interview copilot |

## 1. Mục đích

Tài liệu này xác định yêu cầu có thể kiểm thử của backend và việc tích hợp với frontend Nexora. Nó là baseline giữa Product Owner, frontend, backend, QA và vận hành. Mỗi yêu cầu có mã, mức ưu tiên và tiêu chí kiểm tra; thay đổi sau khi duyệt phải được ghi trong changelog/issue.

## 2. Bối cảnh, mục tiêu và giới hạn

### 2.1 Problem statement

Ứng viên thường luyện từng phần rời rạc (CV, câu hỏi, STAR) và không biết câu trả lời thiếu gì hoặc cần luyện tiếp điều gì. Nexora cung cấp một vòng lặp cá nhân hoá: **CV/JD → luyện → feedback có rubric → luyện lại → theo dõi tiến bộ**.

### 2.2 Mục tiêu business MVP

- Một candidate hoàn tất một mock interview được cá nhân hoá và xem report sau khi đăng nhập.
- Gói trả phí được kích hoạt đúng sau xác nhận payment webhook; quota không thể bị bypass từ client.
- Dữ liệu CV, transcript và report chỉ hiển thị cho chủ sở hữu hoặc admin được uỷ quyền.

### 2.3 Ngoài phạm vi MVP

- Gợi ý đáp án bí mật trong một buổi phỏng vấn tuyển dụng thật.
- Tự động nộp đơn, job marketplace, ATS cho nhà tuyển dụng.
- Lưu hoặc phân tích video/audio mặc định.
- Workspace tổ chức, coach nhiều học viên và mobile app native.

## 3. Stakeholders và actors

| Stakeholder/actor | Nhu cầu | Quyền |
| --- | --- | --- |
| Guest | Hiểu sản phẩm trước khi cam kết. | Xem landing, pricing, demo read-only. |
| Candidate | Luyện riêng tư, nhận feedback hữu ích, quản lý chi phí. | Tạo/sửa/xoá dữ liệu của chính mình, mua gói. |
| Administrator | Hỗ trợ billing/vận hành minh bạch. | Chức năng admin giới hạn, audit log bắt buộc. |
| Payment provider | Báo trạng thái thanh toán. | Chỉ gọi webhook có signature hợp lệ. |
| AI provider | Trả text analysis/scoring. | Không truy cập trực tiếp từ browser. |
| System operator | Deploy, theo dõi, khôi phục. | Không dùng dữ liệu người dùng ngoài mục đích vận hành. |

## 4. Tổng quan hệ thống

### 4.1 System context

```text
Candidate browser -> Nexora frontend -> Nexora .NET API -> PostgreSQL
                                            |-> private file storage
                                            |-> job worker -> AI provider
                                            `-> payment provider webhook
```

### 4.2 Trạng thái actor

- **Guest:** chưa có session; mọi mutation API trả `401`.
- **Authenticated candidate:** session hợp lệ, có profile và entitlement.
- **Entitled candidate:** có plan/quota hợp lệ cho action yêu cầu AI.
- **Admin:** role server-side riêng; không được suy ra từ email/browser.

### 4.3 Fixed technical baseline

- .NET 10 LTS, ASP.NET Core 10 Web API, Entity Framework Core 10 và PostgreSQL.
- ASP.NET Core Identity là source of truth cho credential/role.
- C# version do .NET 10 SDK cung cấp trừ khi repository pin version được hỗ trợ khác.
- REST JSON base path `/api/v1`; modular monolith Three-Layer Presentation → Business → Data, integrations qua adapters.

## 5. Use cases chính

| UC | Precondition | Main success outcome | Exception quan trọng |
| --- | --- | --- | --- |
| UC-01 Đăng nhập | Guest mở login | Có authenticated session và profile | OAuth/email thất bại không tạo session dở dang. |
| UC-02 Upload CV | Candidate đã login | Resume `ready`, chỉ owner truy cập được | MIME/size sai hoặc extract fail báo trạng thái an toàn. |
| UC-03 Phân tích CV–JD | Resume/JD hợp lệ, còn quota | Analysis `completed`, có report/version | Hết quota không tạo AI job; job fail có retry/hoàn quota theo rule. |
| UC-04 Luyện phỏng vấn | Context hợp lệ, còn quota | Session active, câu hỏi và answers được lưu | Refresh không mất session; owner check mọi request. |
| UC-05 Nhận report | Session completed | Report có rubric/evidence/action plan | Không có report trước completion. |
| UC-06 Mua gói | Candidate login | Payment paid kích hoạt entitlement một lần | Webhook trùng/lỗi signature không thay đổi billing. |
| UC-07 Xoá dữ liệu | Candidate login | Dữ liệu được xoá/anonymise theo retention | File/object không còn public URL. |

## 6. Functional requirements

### 6.1 Identity and access

| ID | Requirement | Priority | Verification |
| --- | --- | --- | --- |
| FR-AUTH-01 | Hệ thống hỗ trợ email/password và Google OAuth; password không tự xử lý/lưu plaintext. | Must | Integration test register/login/OAuth callback. |
| FR-AUTH-02 | Backend xác thực session/token ở mọi mutation; frontend localStorage không phải authority. | Must | Gọi API mutation không token nhận `401`. |
| FR-AUTH-03 | Mọi personal resource kiểm tra ownership/action policy. | Must | User A dùng ID của User B nhận `404` hoặc `403`. |
| FR-AUTH-04 | User có thể xem/cập nhật profile, export core data và yêu cầu xoá account/data theo configurable retention policy. | Must | Account deletion/export test, revoke session test; final periods theo DEC-03. |

### 6.2 Plans, payment and usage

| ID | Requirement | Priority | Verification |
| --- | --- | --- | --- |
| FR-BILL-01 | Plan catalogue (Free/Basic/Weekly/Pro) được backend trả về, không tin giá từ client. | Must | Request giả amount/plan bị từ chối. |
| FR-BILL-02 | Tạo checkout tạo order `pending` với price snapshot và idempotency key. | Must | POST lặp cùng key chỉ có một order. |
| FR-BILL-03 | Chỉ webhook đã verify signature mới chuyển order sang `paid`. | Must | Webhook sai signature không thay DB. |
| FR-BILL-04 | Payment event trùng không tạo subscription/usage thêm. | Must | Gửi event hai lần, DB có một fulfillment. |
| FR-BILL-05 | Service xử lý quota transactionally bằng immutable `reserve`, `consume`, `void`, `adjustment` events theo BR-08. | Must | Concurrent request không vượt quota; failure boundary produce đúng event. |
| FR-BILL-06 | User xem plan, usage và basic order history của chính mình trong main journey. | Must | Dashboard amount khớp usage events. |
| FR-BILL-07 | Trước provider thật, `IPaymentProvider` + `FakePaymentProvider` phải chứng minh pending order → verified simulated webhook → paid → entitlement exactly once, kể cả duplicate event. | Must | Integration tests fake webhook/signature/idempotency. |

### 6.3 Resume and CV–JD analysis

| ID | Requirement | Priority | Verification |
| --- | --- | --- | --- |
| FR-CV-01 | Chỉ cho upload PDF/DOCX, giới hạn size cấu hình; validate content type server-side. | Must | File MIME giả bị từ chối. |
| FR-CV-02 | Upload qua signed URL ngắn hạn vào storage private; API chỉ lưu metadata sau upload. | Must | URL hết hạn và non-owner download bị từ chối. |
| FR-CV-03 | Extract và analysis chạy job bất đồng bộ với state `queued/processing/completed/failed`. | Must | UI có thể poll state và retry theo rule. |
| FR-CV-04 | Analysis lưu resume/JD version, model/prompt/schema version, result và timestamp. | Must | Có thể audit result về input/version. |
| FR-CV-05 | `POST /resume-analyses` có thể kế thừa Primary Resume và Career Goal context cho các trường bị bỏ trống; explicit owner-scoped input được ưu tiên và context hiệu lực được snapshot khi tạo analysis. | Must | Default/override, ownership, readiness, idempotency và immutable snapshot integration tests. |
| FR-CV-06 | Owner có thể xoá riêng resume qua `DELETE /resumes/{id}`; resume được soft-delete và ẩn khỏi lựa chọn/đọc hiện hành, Primary Resume được clear transactionally, private object được dọn bất đồng bộ có retry bền vững, còn analysis/interview history được giữ. Resume đã xoá không còn là current AI context, kể cả trong active interview đang giữ historical ResumeId. | Must | Unknown/foreign IDs cùng opaque 404; owner delete/replay 204; current selectors/evidence/interview AI context exclude tombstone; history retained; storage retry and queued-worker race integration tests. |

### 6.4 Mock interview and report

| ID | Requirement | Priority | Verification |
| --- | --- | --- | --- |
| FR-INT-01 | Candidate chọn role, seniority, interview type, difficulty và tuỳ chọn CV/JD trước khi start. | Must | Invalid/foreign CV-JD ID bị từ chối. |
| FR-INT-02 | Session dùng canonical lifecycle `draft → starting → active → completing → completed`, `starting → failed`, `active → abandoned`; lưu question, official answer, timestamps/version và re-open sau refresh không mất dữ liệu. Chỉ `active` nhận official answer, submission phải idempotent. | Must | State/invalid-transition/concurrency và create-answer-reload integration tests. |
| FR-INT-03 | Khi entitlement cho phép phần trả phí, AI có thể sinh follow-up theo evaluation của một câu hỏi behavioral có thiếu bằng chứng; phải lưu kind/topic/parent để tái hiện session. Free flow luôn dùng ba primary topic chuẩn trước khi continuation. | Should | Same session GET trả thứ tự question ổn định; không gọi AI cho câu bị paywall. |
| FR-INT-04 | Report hiển thị rubric scores, evidence từ transcript, strengths, gaps và action plan. | Must | Report không chỉ có điểm tổng; rubric fields bắt buộc. |
| FR-INT-05 | Audio/video và speech metrics là opt-in; consent được lưu trước khi bắt đầu recording. | Could | Không tạo recording khi chưa consent. |

#### Candidate loop extensions

The first three free primary topics are server-owned and deterministic. Q1 is
`self_introduction`; Q2 reflects the selected interview type; Q3 keeps that
mode or uses the available JD/CV targeting fallback defined by the interview
topic policy. The selected mode must be meaningfully exposed before the free
question limit, and the AI provider never selects the topic.

| ID | Requirement | Priority | Verification |
| --- | --- | --- | --- |
| FR-INT-06 | `POST /interviews` may resolve missing role, seniority, JD and resume context from an owner Career Goal and Primary Resume; the resolved snapshot is stored on the session. | Must | Goal ownership, ready-resume/JD validation and immutable historical snapshot tests. |
| FR-INT-07 | `POST /interviews/{id}/practice-again` creates a new owner-scoped interview from a completed source report, optionally focused on a source question, rubric weakness or recommendation. | Must | New ID, source/report immutability, canonical focus and idempotency tests. |
| FR-INT-08 | Answer evaluation may persist a separate nullable illustrative `sampleAnswer` (`star`, `self_intro`, `technical` or `direct`) selected to fit the question. It may contain hypothetical example details but is never candidate evidence/fact and never contributes to scores, strengths, report transcript/evidence, skills, progress or recommendations. `candidateAnswer` remains the factual source and `improvedAnswer` remains its grounded rewrite. Invalid/unusable sample content becomes null/absent without invalidating otherwise-valid core evaluation or causing an additional AI call. | Should | STAR/self-introduction/technical/direct examples use the appropriate structure; hypothetical sample facts do not enter candidate-grounded fields; malformed sample degrades to null while core evaluation remains valid; old evaluation JSON without the property deserializes and reads as null. |
| FR-DASH-02 | Owner-scoped interview, resume-analysis and Job Description history reads provide bounded deterministic pagination or navigation without raw answer/CV/provider payloads. | Must | Auth, isolation, ordering, pagination and no-leak tests. |

Career Goal and Primary Resume are context sources, not replacements for their
domain resources. Explicit start fields remain backward compatible and may
override goal defaults only after owner/ready validation. Practice-again uses a
new interview quota reservation under the normal ledger rules; it never refunds
or mutates the source session/report. Recommendation responses may include
nullable actionable `practice_again` metadata (`sourceInterviewId`,
`sourceQuestionId`, `focusTopic`, `suggestedInterviewType` and canonical reason)
so the client does not reconstruct domain joins.

A soft-deleted Resume may remain linked to an existing InterviewSession for
history, but it is not usable resume context. Future answer evaluation,
automatic next-question generation, paid continuation and follow-up generation
must pass no ResumeProfile and select topics as if no Resume were available.
Previously-issued questions, persisted answers/evaluations, reports and the
historical ResumeId are not rewritten. Practice Again still creates a new
session through canonical owner/ready/non-deleted Resume validation and rejects
an inherited tombstoned Resume.

### 6.5 Practice, dashboard and support

| ID | Requirement | Priority | Verification |
| --- | --- | --- | --- |
| FR-PRAC-01 | STAR lưu draft/version/feedback gắn user. | Should | User A không đọc draft User B. |
| FR-PRAC-02 | Scenario attempt lưu đề, answer, feedback và trạng thái. | Should | Attempt có lịch sử riêng theo user. |
| FR-DASH-01 | Basic dashboard/history trả activity, quota và report summary cần để tiếp tục main journey từ DB. | Must | Không seed mock localStorage ở production; owner isolation test. |
| FR-ADM-01 | Admin billing/support action có RBAC và audit log. | Should | Non-admin endpoint trả `403`; action có log. |

## 7. Business rules

| ID | Rule |
| --- | --- |
| BR-01 | Guest có thể xem demo nhưng không được upload, save, start job hay checkout. |
| BR-02 | Entitlement là nguồn sự thật của server; client chỉ hiển thị convenience state. |
| BR-03 | Usage event immutable; correction tạo adjustment event, không sửa lịch sử âm thầm. |
| BR-04 | Interview lifecycle canonical là `draft → starting → active → completing → completed`, thêm `starting → failed` và `active → abandoned`. Chỉ `active` nhận official answer. Completion đúng một lần, report generation idempotent, transition dùng optimistic concurrency/version; terminal state immutable trừ audited administrative/recovery process. |
| BR-05 | Score là coaching estimate, không phải đánh giá tuyển dụng; report phải có disclaimer. |
| BR-06 | CV/JD chỉ được dùng cho analysis/session mà candidate chọn; không dùng training model nếu chưa có consent riêng. |
| BR-07 | Refund không xoá usage history. Khi payment đã refund, entitlement liên quan chuyển `revoked` theo policy DEC-02; phần quota chưa dùng không còn khả dụng và interview usage đã `consume` không tự cộng lại. Mọi goodwill credit phải là adjustment event có lý do/audit log. |
| BR-08 | Start transaction reserve interview quota, create session `starting` và outbox/job idempotently. Reservation được consume khi first usable question đã persist thành công và session transition thành công `starting → active`; question persistence + consume + activation là một coherent PostgreSQL transaction khi khả thi. Terminal failure trước question persistence và activation phải atomically `void` + `starting → failed`. Sau activation, disconnect/refresh/rời trang/không trả lời hoặc later AI/report failure không auto-void. Report retry idempotent và không charge thêm; terminal report failure tạo credit adjustment tự động hoặc support case có audit. |

## 8. External interface requirements

| Interface | Requirement |
| --- | --- |
| Frontend/API | REST JSON `/api/v1`, UTC ISO-8601, standard error envelope, API versioning. |
| AI | `IAiProvider` adapter; Development/internal traffic defaults to configuration-driven `GeminiAiProvider` and may select optional `DeepSeekAiProvider` for local text evaluation, while deterministic test doubles stay in the test project; timeout, bounded retry, structured schema/semantic validation, token/cost telemetry; no client key/provider type leak. `GeminiDocumentOcrProvider` remains the document fallback. DEC-01 still gates production AI. |
| Payment | `IPaymentProvider`; `FakePaymentProvider` trước DEC-02; hosted production checkout, signature verification and idempotent webhook. |
| Storage | `IStorageProvider`; `LocalStorageProvider`/development adapter được phép nhưng không dùng production; `R2StorageProvider` cung cấp private production-like objects, signed PUT/GET where supported, file checksum and lifecycle policy. |
| Email | Transactional email adapter for verify/reset/payment receipt; no sensitive content in URL. |

## 9. Non-functional requirements

### 9.1 Capacity profile

Mọi kiểm thử hiệu năng/khả dụng MVP dùng dataset staging tối thiểu: **1.000 registered candidates, 500 CV/JD documents, 2.000 interview sessions, 10.000 answers, 1.000 reports, 100 concurrent active users**. Dataset có dữ liệu synthetic, không dùng CV thật. Profile này tương ứng mục tiêu vận hành vài chục–vài trăm user hiện tại, không phải capacity promise dài hạn.

| ID | Requirement | Target/verification |
| --- | --- | --- |
| NFR-PERF-01 | API CRUD performance | P95 < 500 ms tại baseline staging: 50 VUs/15 RPS steady 10 phút và burst 100 VUs/30 RPS 60 giây; error rate < 1%. |
| NFR-PERF-02 | Long-running work | API returns job/session state quickly; worker handles retries. |
| NFR-REL-01 | Availability | 99.5% monthly API target, excluding announced maintenance. |
| NFR-REL-02 | Recovery | Daily DB backup; restore rehearsal per release cycle. |
| NFR-SEC-01 | API authorization | Test BOLA/BFLA for every resource/function; OWASP identifies object authorization as a major API risk. |
| NFR-SEC-02 | Secrets | Secrets only in secret manager/deploy environment; rotation procedure documented. |
| NFR-PRIV-01 | Retention | CV/transcript/recording retention and deletion window are configurable and disclosed. |
| NFR-OBS-01 | Traceability | Request/job/payment correlation ID, structured logs, alert thresholds. |
| NFR-QUAL-01 | Quality gate | Build, format, unit, integration and security scan must pass before production. |

## 10. Acceptance and traceability baseline

| Release scenario | Related requirements | Proof before go-live |
| --- | --- | --- |
| New user → plan → payment sandbox | FR-AUTH-01, FR-BILL-01..04 | E2E recording + automated webhook tests. |
| Upload → analysis → report | FR-CV-01..04, FR-INT-04 | Automated integration test + staging smoke test. |
| Owner isolation | FR-AUTH-03, FR-CV-02, FR-PRAC-01 | Negative tests for each resource endpoint. |
| Individual resume deletion | FR-CV-06, NFR-SEC-01, NFR-PRIV-01 | Owner/idempotency, primary clearing, retained history, worker/storage retry tests. |
| Quota race | FR-BILL-05, BR-02..04 | Concurrent integration test. |
| Account deletion | FR-AUTH-04, NFR-PRIV-01 | DB/storage deletion job test. |

Use-case specification, sequence/class/package/deployment diagrams và ma trận FR → UC → API → test được quản lý tại [11-analysis-design-models.md](11-analysis-design-models.md). Đây là artefact thiết kế trước implementation; source consistency evidence được ghi ở appendix trong design model.

## 11. Deferred production enablement decisions

Các quyết định dưới đây **không block backend/local development, Phases 0–3 hoặc integration test dùng internal Gemini/DeepSeek development adapters và test-project doubles**. Chúng chỉ block real production capability tương ứng:

1. **DEC-01:** production AI provider/model và production per-user/global budgets, alert/circuit-break controls.
2. **DEC-02:** production Vietnamese payment provider; refund, invoice và tax handling.
3. **DEC-03:** final retention periods cho CV/JD/transcript/recording/logs và approved Terms/Privacy/AI/recording text.
4. **DEC-04:** production hosting/storage vendors, domains, mail provider và infrastructure accounts.

Pricing/plan business values vẫn phải được xác định theo ticket liên quan nhưng không phải lý do để block repository/foundation work. Không suy ra production decision từ Gemini, test-project double, local storage hoặc vendor example.
