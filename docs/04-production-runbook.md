# Runbook production

**Status:** Approved operational baseline; production vendors/legal gates deferred  
**Last updated:** 2026-08-21

This runbook targets .NET 10 LTS / ASP.NET Core 10 / EF Core 10. DEC-01–04 do not block development or integration testing; they must be resolved only before enabling/deploying the affected real production capability.

## Môi trường và secrets

Tạo `development`, `staging`, `production` tách biệt: database, bucket, OAuth redirect URL và payment keys riêng. Chỉ khai báo secret trong dashboard deploy/secret manager:

```text
DATABASE_URL
AUTH_SECRET
GOOGLE_CLIENT_ID / GOOGLE_CLIENT_SECRET
AI_PROVIDER_API_KEY
PAYMENT_PROVIDER_SECRET / PAYMENT_WEBHOOK_SECRET
STORAGE_* 
OBSERVABILITY_DSN
```

Không commit `.env`, CV mẫu có dữ liệu thật hoặc webhook payload production.

## Pipeline phát hành

1. Pull request: `dotnet restore`, `dotnet format style --verify-no-changes`, `dotnet format analyzers --verify-no-changes`, `dotnet build`, `dotnet test` (unit/integration) và secret scan khi corresponding projects tồn tại. LF-vs-CRLF-only differences are not a correctness failure.
2. Deploy preview: migration được kiểm thử trên staging, chạy smoke test login/upload/interview.
3. Production: backup DB, chạy migration tương thích ngược, deploy API/frontend, smoke test và theo dõi lỗi 30 phút.
4. Rollback: rollback application trước; migration phải có kế hoạch forward-fix hoặc migration rollback đã thử nghiệm.

## Bảo mật tối thiểu

- Cookie HTTP-only, `Secure`, `SameSite`; CSRF protection nếu dùng cookie session.
- Rate limit bằng ASP.NET Core rate-limiting middleware. Các giá trị production initial (configuration, không hard-code): login **5 attempts/15 phút/IP** và **10/15 phút/email**; refresh **30/giờ/session**; upload **10/giờ/user**; checkout **5/giờ/user**; start AI job **10/giờ/user** ngoài quota plan; submit answer **20/5 phút/session**. Trả `429` + `Retry-After`; calibrate lại sau load test và phê duyệt thay đổi qua config review.
- Validate detected MIME/signature, kích thước và extension; malware scanning là defence tùy khả năng, không phải guarantee rằng file an toàn.
- TLS bắt buộc, CSP phù hợp, CORS allowlist theo domain production.
- Mã hoá dữ liệu khi lưu/truyền; định nghĩa retention và endpoint xoá tài khoản/dữ liệu.

## Observability và alert

- Mỗi request/job/event có `requestId`/`correlationId`.
- Ghi structured logs: actor, resource, event, duration, error code; không ghi CV, token hoặc nội dung nhạy cảm nguyên văn.
- Alert: API 5xx > 2%, queue lag, AI failure, webhook verify fail, payment pending bất thường, quota transaction fail.
- Dashboard theo dõi: latency, error rate, AI cost/job, payment conversion, job success rate.
- Poll `/api/v1/health/operations`; trạng thái `Degraded` nghĩa là ít nhất một ngưỡng `OperationsHealth` bị vượt: queue lag, payment pending hoặc recent job/deletion failure. Route chỉ trả trạng thái tổng quát, không lộ count/ID ra response mặc định.
- Log hoàn tất request gồm request ID, actor ID, method, path, status và duration; job/payment log chỉ chứa correlation/resource IDs, outcome, duration và exception type, không chứa body, CV/JD/transcript, token hoặc raw provider error.
- Alert delivery/dashboard backend cụ thể được cấu hình cùng hạ tầng đã duyệt theo DEC-04; source hiện cung cấp vendor-neutral structured signals và health state.

## Performance baseline để test staging

Cho MVP (vài chục–vài trăm user), gate staging ban đầu là **50 virtual users trong 10 phút, tải ổn định 15 RPS CRUD/API**, cộng **burst 100 virtual users trong 60 giây, tối đa 30 RPS**. P95 endpoint synchronous không-AI dưới 500 ms, error rate dưới 1%; job creation chỉ enqueue, không chờ model. AI load testing và k6 CRUD script không thuộc luồng development nội bộ hiện tại; chỉ thực hiện lại bằng dữ liệu/đối tượng được phê duyệt khi chuẩn bị staging. Đây là baseline kỹ thuật, không phải cam kết capacity; chỉnh lại khi có số liệu production.

## Backup/restore rehearsal

T-10 dùng `scripts/verify-postgres-backup.ps1 -ConfirmIsolatedTarget`. Cung cấp connection string qua `NEXORA_BACKUP_SOURCE` và `NEXORA_RESTORE_TARGET`; target phải là database cô lập/disposable có tên thể hiện `isolated`, `restore`, `drill` hoặc `test`. Sau restore, trỏ một API instance vào target rồi đặt `NEXORA_RESTORE_API_READ_URL` tới authenticated read endpoint và `NEXORA_RESTORE_API_TOKEN` của synthetic account. Script chỉ pass khi `pg_dump`, `pg_restore` và API read canonical đều thành công.

Không chạy drill vào production target. Không ghi connection string/token vào command, log hoặc source control.

## Checklist go-live

- [ ] Domain, DNS, HTTPS và OAuth redirect URLs production hoạt động.
- [ ] Backup/restore DB đã test; retention CV/transcript được phê duyệt.
- [ ] Webhook payment được verify bằng sandbox và retry idempotent.
- [ ] RLS/authorization integration tests pass.
- [ ] Feature flags cho phép tắt AI/payment/upload độc lập.
- [ ] Terms, Privacy Policy và consent audio/video có URL production.
- [ ] Owner trực vận hành và quy trình incident/rollback đã xác định.

## Production enablement gates

- **DEC-01:** production AI provider/model and budgets approved before real production AI traffic; internal Gemini development traffic remains allowed.
- **DEC-02:** production Vietnamese payment/refund/invoice/tax decision approved before real payments; `FakePaymentProvider` verified webhook flow remains allowed.
- **DEC-03:** final retention periods and approved legal/privacy text completed before processing affected personal data in production.
- **DEC-04:** hosting/storage vendors, domains, mail and infrastructure accounts completed before production deployment.

Development/testing storage may use `LocalStorageProvider`; production-like configuration may use `R2StorageProvider` with private objects and validated HTTPS endpoint/credentials. A2 now supplies the durable database-backed R2 upload-intent, signed PUT and finalize path, so `Features:Upload=true` is technically allowed only when `Storage:Provider=r2`; Production + local upload remains a startup failure. Local filesystem storage is never a production option. Actual production account/hosting enablement remains subject to DEC-04, and the exact deferred-decision wording is canonical in [07-architecture-decisions.md](07-architecture-decisions.md#production-enablement-decisions-dec-01-through-dec-04).

## Incident response tối thiểu

| Mức | Ví dụ | Hành động ban đầu |
| --- | --- | --- |
| P1 | Lộ secret, truy cập dữ liệu trái phép, payment xử lý sai diện rộng | Disable integration/rotate secret, giữ evidence log, thông báo owner ngay, dừng deploy. |
| P2 | API không hoạt động, AI/payment job backlog lớn | Rollback release gần nhất hoặc bật feature flag off, kiểm tra queue/database. |
| P3 | Một job/report bị lỗi | Ghi request/job ID, retry theo policy, tạo issue nếu lặp lại. |

Sau P1/P2 phải có postmortem: timeline, ảnh hưởng, nguyên nhân, corrective action và owner/due date.
