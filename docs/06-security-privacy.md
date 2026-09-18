# Security and Privacy Specification — Nexora

**Status:** Approved security baseline; retention/legal decision deferred  
**Last updated:** 2026-09-18

## 1. Dữ liệu và mức nhạy cảm

| Dữ liệu | Mức | Kiểm soát |
| --- | --- | --- |
| Email, profile | Personal | Owner-only, minimal log, delete request. |
| CV, JD, transcript, STAR answers | Sensitive career data | Private storage, owner authorization, signed URL, retention/deletion. |
| Audio/video | Highly sensitive | Disabled by default; explicit consent, lifecycle policy, no public URL. |
| Payment references | Financial metadata | Không lưu card data; chỉ provider IDs/status; webhook verify. |
| Secret/token | Critical | Secret manager only, never log/client/source. |

A10 keeps voice input browser-side: microphone audio and an unconfirmed/raw STT
transcript are not sent to or stored by the backend. Only the user-confirmed
text is submitted as the ordinary interview answer. Any future backend STT or
audio retention requires a separate provider, consent, lifecycle, and retention
decision.

## 2. Threat model và mitigation

| Threat | Control |
| --- | --- |
| User đổi resource ID để đọc CV/report khác | Authorization policy ở mọi service action + negative integration tests. Đây là nhóm rủi ro [BOLA](https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/) của OWASP. |
| Client sửa plan/quota/price | Server derives entitlement/price; usage transaction và immutable events. |
| Webhook giả hoặc replay | Verify signature/timestamp, unique provider event ID, idempotency key. |
| Upload file độc hại | MIME/signature/size validation, private bucket, optional malware scan, no direct serving. |
| Prompt injection từ CV/JD | Treat document as untrusted input, delimit text, forbid tool/data access, constrain output schema, log safe metadata. |
| Secret leaked | Environment secret manager, rotation runbook, scan in CI, least privilege. |

## 3. Privacy requirements

- Trước upload/recording, hiển thị purpose, retention, quyền xoá và link Privacy Policy.
- CV/JD/transcript không được dùng để training AI model nếu chưa có opt-in riêng, rõ ràng.
- Người dùng có thể export dữ liệu core và yêu cầu xoá account; task xoá có audit status.
- Owner có thể xoá riêng một resume bằng soft delete: current-library/profile/evidence reads stop selecting it immediately, the Primary Resume pointer is cleared transactionally, and private-object deletion is retried durably through the configured storage provider. Analysis/interview history and snapshots are retained; account-wide deletion remains a separate lifecycle.
- Chỉ gửi amount dữ liệu cần thiết tới AI provider; không đính kèm dữ liệu account/payment không liên quan.
- Log chỉ chứa IDs/correlation IDs, không chứa CV/transcript nguyên văn trừ khi có secure debug exception đã phê duyệt.

### Sentry telemetry boundary (A11)

- Sentry chỉ nhận exception đã được capture ở API/Worker cùng metadata vận hành an toàn (`service`, environment, release, `request_id` hoặc worker cycle ID).
- `SendDefaultPii` luôn là `false`; không gắn user/email/name. Body, query string, Authorization/Cookie, access token, provider secrets/payloads, CV/JD, answer/transcript, prompt/response và uploaded document đều bị loại khỏi event.
- Business 4xx và client cancellation không tạo issue. Sentry là kênh bổ sung; structured logs, health checks và trạng thái job vẫn là nguồn fallback khi DSN trống hoặc Sentry không khả dụng.

## 4. Vietnam compliance checkpoint

Nexora xử lý thông tin nhận diện, CV, lịch sử nghề nghiệp và có thể cả recording. Trước go-live tại Việt Nam, Product Owner phải yêu cầu legal review về [Nghị định 13/2023/NĐ-CP](https://vanban.chinhphu.vn/default.aspx?docid=207759&pageid=27160), có hiệu lực từ 01/07/2023. Đây không phải tư vấn pháp lý. Theo DEC-03, final retention periods và approved legal/privacy text còn deferred: điều này không block development bằng configurable policies/synthetic data nhưng block production processing khi disclosure/approval tương ứng chưa hoàn tất.

Checklist kỹ thuật/sản phẩm cần đưa cho legal review:

- Notice có thể lưu/in: mục đích, loại dữ liệu, bên xử lý/AI provider, retention và contact.
- Consent riêng cho recording, dữ liệu dùng ngoài mục đích cung cấp dịch vụ hoặc chuyển dữ liệu ra nước ngoài nếu có.
- Data-minimisation: chỉ thu CV/JD/transcript cần cho feature; user có luồng access/export/delete.
- Có inventory data flow và DPA/điều khoản với storage, AI, email, payment providers.
- Có owner xử lý incident, evidence of consent và process thực hiện yêu cầu dữ liệu cá nhân.

## 5. Secure SDLC

Process áp dụng theo bốn nhóm thực hành của [NIST SSDF](https://csrc.nist.gov/projects/ssdf): chuẩn bị tổ chức, bảo vệ software, tạo software an toàn và phản hồi vulnerability. Tối thiểu gồm PR review, dependency/secret scan, threat review khi thêm integration, vulnerability triage và post-incident corrective action.

## 6. Phase 4 security review evidence — 2026-08-25

- BOLA/BFLA: user-owned reads/mutations derive owner ID from authenticated claims; cross-owner interview/report/analysis paths and guest mutations have negative integration evidence.
- Financial boundary: catalogue/price/quota remain server-owned; fake webhook signature, timestamp, replay and idempotent fulfillment have T-03/T-04/T-05 evidence.
- Sensitive input/output: upload signature/size/MIME checks, private storage keys, bounded untrusted AI input and allowlisted export DTOs are covered by integration tests.
- Production fail-closed: internal-development Gemini/DeepSeek AI, Fake Payment and the development upload adapter cannot be enabled in Production. `R2StorageProvider` plus the durable A2 upload-intent/finalize path is accepted only with validated private R2 configuration; `Storage:Provider=local` with `Features:Upload=true` remains rejected. DEC-01, DEC-02, DEC-03 and DEC-04 still gate the corresponding production enablement.
- Logging review: structured signals contain IDs, status, duration and exception type only; request bodies, query strings, credentials, CV/JD/transcript and raw provider errors are excluded.
- Remaining go-live evidence: approved domain/auth-cookie/CSRF browser E2E, production CORS/TLS, DEC-01–04, T-10 isolated restore, staging load/DAST and external alert delivery are still required. This review does not mark those gates complete.
