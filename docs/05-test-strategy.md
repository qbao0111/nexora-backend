# Test Strategy — Nexora .NET 10 MVP

**Status:** Approved implementation baseline; canonical Definition of Done  
**Last updated:** 2026-08-21

## 1. Mục tiêu

Chứng minh requirement trong SRS hoạt động đúng, đặc biệt là quyền dữ liệu, quota, payment idempotency và các luồng AI không đồng bộ. Test không chỉ xác nhận UI hiển thị đẹp.

## 2. Test pyramid

| Tầng | Phạm vi | Công cụ/đầu ra |
| --- | --- | --- |
| Unit | Business services, state transition, pricing/quota/rubric | xUnit + mocks/fakes; chạy mỗi commit. |
| Integration | API + auth + PostgreSQL + migrations + storage fake | `WebApplicationFactory`/Testcontainers; chạy PR. |
| Contract | Payment/AI/storage adapter | Provider sandbox hoặc recorded fixture; schema/version test. |
| E2E | Browser flows quan trọng | Playwright: guest/login/upload/interview/checkout sandbox. |
| Non-functional | Load, security, restore | k6, DAST/manual authorization matrix, backup-restore drill. |

## 3. Test cases bắt buộc trước production

| ID | Scenario | Expected result |
| --- | --- | --- |
| T-01 | Guest POST upload/start interview | `401`, không tạo DB record/job. |
| T-02 | User A GET/PATCH/DELETE ID của User B | `404`/`403`, không lộ metadata. |
| T-03 | Hai request start interview với quota còn 1 | Tối đa một reservation/session `starting`; success tạo đúng một question, consume và session `active`. |
| T-04 | Payment webhook gửi lại cùng event | Một payment event, một entitlement. |
| T-05 | Webhook signature sai | `401/400`, không thay đổi order. |
| T-06 | File .exe đổi tên .pdf | Rejected, không cấp storage final record. |
| T-07 | AI timeout sau reserve quota | Terminal failure trước first usable question persistence và `starting → active`: atomic `void` + `failed`; success: atomic question persistence + `consume` + `active`; sau activation không auto-void, report retry idempotent/miễn phí và terminal failure tạo adjustment/support audit theo BR-08. |
| T-08 | Refresh sau answer | Question/answer order và report không mất. |
| T-09 | Account deletion | Personal records/object theo retention policy được xoá/anonymise. |
| T-10 | Restore backup vào môi trường cô lập | API đọc được dữ liệu hợp lệ sau restore. |

## 4. Quality gates

Khi corresponding projects tồn tại, baseline local/CI bắt buộc gồm `dotnet restore`, `dotnet build` và `dotnet test`. Implementation chưa complete khi relevant tests fail.

| Gate | Điều kiện pass |
| --- | --- |
| Pull request | Build, format, unit/integration, dependency/secret scan pass. |
| Staging | Migration fresh + upgrade pass; E2E critical paths pass; payment sandbox pass. |
| Production | Smoke test health/auth/plan read; error rate và queue lag bình thường sau deploy. |

Coverage phần trăm không thay thế test risk-based. Mục tiêu initial: business services quan trọng ≥80% line coverage; 100% scenario trong bảng T-01..T-10 phải có automated hoặc checklist evidence.

## 5. Canonical Definition of Done

Đây là checklist DoD duy nhất. `PROJECT-SPEC` và `10-delivery-plan` chỉ tham chiếu section này; SRS acceptance là **evidence theo requirement**, không phải DoD thứ hai.

- SRS ID, API contract, authorization rule, state transition và data/retention impact được review.
- Code, EF migration (nếu có), unit/integration test và DTO/API documentation được merge.
- Required test in T-01..T-10 hoặc test case mới có automated evidence; format/build/dependency/secret scan pass.
- Logging, metric, alert/error mapping và rollback/feature flag được thêm theo mức rủi ro.
- Staging migration + E2E smoke test pass; traceability requirement được cập nhật.

Các tài liệu khác chỉ được tham chiếu checklist này, không tạo competing DoD.
