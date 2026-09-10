# Kiến trúc và design patterns

**Status:** Approved implementation baseline  
**Last updated:** 2026-08-21

## Kiến trúc được chọn: Modular Monolith Three-Layer

Giữ frontend HTML/JS hiện tại trên Vercel; bổ sung một ASP.NET Core Web API độc lập. Một codebase, một database PostgreSQL và một worker xử lý job nền là đủ cho vài chục đến vài trăm người dùng. Không gọi AI hay payment provider trực tiếp từ trình duyệt.

```text
Browser -> Vercel static frontend -> ASP.NET Core Web API
                                      |-> ASP.NET Core Identity / Google OAuth
                                      |-> PostgreSQL (EF Core)
                                      |-> Object storage (signed URLs)
                                      |-> Queue -> .NET Worker -> AI / document extraction
                                      `-> Payment provider webhook
```

### Technical baseline

- Runtime/API: .NET 10 LTS, ASP.NET Core 10 Web API; dùng C# version từ .NET 10 SDK trừ khi project pin version được hỗ trợ khác.
- Database: PostgreSQL + Entity Framework Core 10.
- Identity: ASP.NET Core Identity với EF Core PostgreSQL store.
- File: `IStorageProvider`; `Storage:Provider=local` dùng `LocalStorageProvider` cho development/testing, còn `Storage:Provider=r2` dùng `R2StorageProvider` với private S3-compatible objects. A2 thêm durable upload intent + signed PUT/finalize; final production account/hosting choice vẫn thuộc DEC-04.
- Jobs: .NET worker/background job mechanism với state bền vững, timeout và bounded retry; chọn package/queue cụ thể khi implementation cần, không biến nó thành microservice.
- Observability: structured logs, metrics/traces và uptime/error monitoring; production vendor thuộc DEC-04.

## Ba tầng và patterns tối thiểu

| Pattern | Áp dụng |
| --- | --- |
| Three-Layer | `controller (presentation) -> service (business) -> repository (data)`; controller không chứa business logic. |
| Repository | Truy cập EF Core/SQL qua repository, giúp test và đổi storage implementation. |
| Service | `InterviewService`, `ResumeService`, `BillingService` và `ReportService` chứa nghiệp vụ. |
| Adapter | Chuẩn hoá `AIProvider`, `PaymentProvider`, `StorageProvider`; thay nhà cung cấp không làm đổi domain logic. |
| Outbox + idempotency | Ghi event cùng transaction; webhook/job có idempotency key để không xử lý lặp. |
| State machine | Resume, analysis, interview và payment có trạng thái hợp lệ, cấm nhảy trạng thái sai. |
| Policy/authorization | `canReadResume`, `canStartInterview`, `canUseEntitlement` chạy server-side trước repository/use case. |

## Cấu trúc source đề xuất

```text
src/
  Nexora.Api/             # Presentation: Controllers, middleware, DTOs
  Nexora.Business/        # Business: services, policies, validators, interfaces
  Nexora.Data/            # Data: DbContext, repositories, EF migrations
  Nexora.Integrations/    # Adapters: AI, payment, storage, email
  Nexora.Worker/          # Bounded background jobs cho tác vụ dài
tests/
  Nexora.UnitTests/
  Nexora.IntegrationTests/
```

## Quy tắc thiết kế

- Request/response được validate bằng FluentValidation trước khi vào service.
- Dùng UUID/ULID; mọi bảng sở hữu dữ liệu có `user_id`, `created_at`, `updated_at`.
- Không cộng/trừ quota bằng thao tác đọc-rồi-ghi ở client. Thực hiện transaction/row lock tại DB.
- Job AI phải có timeout, retry có backoff, giới hạn retry và dead-letter log.
- Prompt, model và phiên bản scoring được lưu cùng output để có thể audit/tái tạo kết quả.
- Dùng `IOptions<T>` cho cấu hình, DI tích hợp sẵn của ASP.NET Core và feature flag cho migration từ localStorage không làm hỏng bản đang chạy.
- Không tách microservice trước khi có số liệu cho thấy API/worker hoặc một module đã là bottleneck; khi đó tách worker AI trước.
- Mọi AI/payment/storage/email call đi qua interface trong Business và adapter trong `Nexora.Integrations`; controller/frontend không gọi provider trực tiếp.
- Controller không dùng `DbContext`; DTO không phải EF entity; repository không quyết định business rule.

## Optional realtime invalidation

Worker resource transitions persist minimal `realtime_notifications` rows atomically
with business state. One API-hosted broadcaster reads PostgreSQL and sends
authenticated SignalR `resourceChanged` events to the JWT `sub` user at
`/hubs/realtime`. Worker never references `IHubContext`; REST remains the only
command/query API. This bridge supports one API instance and requires no external
broker. Details and failure/reconnect semantics: [realtime contract](realtime-notifications.md).
