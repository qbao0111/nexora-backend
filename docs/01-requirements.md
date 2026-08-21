# Product Requirements Document (PRD) — Nexora MVP

**Status:** Approved implementation baseline  
**Last updated:** 2026-08-21

> Đây là tài liệu product ở mức quyết định/ưu tiên. Chi tiết requirement có ID và tiêu chí kiểm thử nằm tại [SRS.md](SRS.md); quyết định scope dựa trên [market research](00-market-research.md).

## 1. Mục tiêu

Nexora hỗ trợ ứng viên phân tích CV theo JD, luyện phỏng vấn AI, luyện tình huống và STAR, sau đó xem báo cáo cải thiện. Người dùng có thể xem trước tính năng khi chưa đăng nhập; các thao tác tạo/lưu dữ liệu hoặc dùng tài nguyên AI yêu cầu đăng nhập và entitlement hợp lệ.

## 2. Vai trò

| Vai trò | Quyền |
| --- | --- |
| Guest | Xem landing/pricing và bản demo tính năng; không tạo dữ liệu. |
| User | Quản lý hồ sơ/CV, sử dụng quota theo gói, xem dữ liệu của chính mình. |
| Admin | Quản lý gói, hỗ trợ đơn hàng và xem log vận hành; không tự động có quyền đọc nội dung CV/trả lời nếu không có quy trình hỗ trợ. |

## 3. Yêu cầu chức năng

### FR-01 — Xác thực và tài khoản

- Đăng ký/đăng nhập bằng email và Google OAuth.
- Phiên đăng nhập do backend xác thực; frontend không được coi `localStorage` là nguồn quyền truy cập.
- Người dùng xem/sửa profile, avatar và đăng xuất mọi thiết bị.

### FR-02 — Gói dịch vụ và quota

- Backend trả danh sách gói `Free`, `Basic`, `Weekly`, `Pro` cùng giá, thời hạn và entitlement.
- Quota interview dùng ledger bất biến `reserve`, `consume`, `void`, `adjustment`; reserve phải nguyên tử ở server trước khi bắt đầu AI/session theo BR-08.
- Mua gói tạo order, chờ webhook đã xác thực từ cổng thanh toán rồi mới kích hoạt entitlement.
- Tất cả webhook phải idempotent để không cộng gói/quota hai lần.

### FR-03 — CV và JD

- Chỉ nhận PDF/DOCX, giới hạn kích thước cấu hình được (mặc định 10 MB), quét MIME thực và lưu bằng signed upload URL.
- Trích xuất text bất đồng bộ; người dùng theo dõi trạng thái `uploaded`, `processing`, `ready`, `failed`.
- Phân tích CV–JD tạo bản kết quả có thể xem lại, với version của prompt/model.

### FR-04 — Phỏng vấn AI

- Tạo session theo role, JD, CV và cấp độ khó.
- Lưu câu hỏi, câu trả lời, timestamp, điểm/feedback và trạng thái session.
- Reservation được `consume` khi câu hỏi phỏng vấn hữu ích đầu tiên đã được persist thành công và session transition thành công `starting → active`; ba thay đổi question + usage + session là một coherent transaction khi khả thi. Terminal failure trước cả question persistence và activation phải `void` reservation và chuyển session sang `failed`. Sau activation, disconnect/refresh/rời trang/không trả lời hoặc lỗi AI/report về sau không tự động void; retry report không tính thêm lượt và terminal report failure xử lý bằng adjustment/support có audit theo BR-08.
- Audio/video, nếu phát hành, phải xin consent rõ ràng và chỉ dùng signed URL; transcript là bản dữ liệu chính của MVP.

### FR-05 — Tình huống, STAR và báo cáo

- Evidence/rubric report và basic dashboard/history phục vụ hành trình chính là Must.
- STAR draft/feedback và scenario/case attempt là Should theo SRS; có thể nằm trong MVP theo capacity nhưng không mặc định là launch blocker.
- Mọi bài làm, feedback, dashboard và report chỉ tổng hợp từ dữ liệu server của người dùng đang đăng nhập.

## 4. Yêu cầu phi chức năng

| Mã | Yêu cầu/tiêu chí |
| --- | --- |
| NFR-01 | 99.5% availability tháng cho API public, trừ thời gian bảo trì công bố. |
| NFR-02 | P95 API đồng bộ không-AI dưới 500 ms; tác vụ AI/file chạy background và có trạng thái. |
| NFR-03 | Không để secret, API key, payment key hoặc thông tin thẻ trong frontend/repository. |
| NFR-04 | Mọi tài nguyên cá nhân phải kiểm tra `userId` tại backend; dùng ID khó đoán không thay thế authorization. |
| NFR-05 | Có backup DB hằng ngày, restore test định kỳ và log lỗi có request ID. |
| NFR-06 | Có kiểm thử unit, integration cho API/authorization và end-to-end cho login, upload, payment webhook, interview. |

## 5. Tiêu chí nghiệm thu MVP production

1. Một user mới đăng ký, mua gói sandbox, nhận entitlement đúng một lần sau webhook.
2. User A không thể đọc/sửa CV, session, report hay file của User B qua URL/API.
3. Upload CV, phân tích và phỏng vấn vẫn hoạt động sau refresh/đăng nhập lại.
4. Khi hết quota, server trả lỗi rõ ràng và không gọi AI.
5. Retry webhook/job không tạo order, usage hoặc report trùng lặp.
6. Có alert khi lỗi API, webhook hoặc job vượt ngưỡng.

## 6. User journey ưu tiên

```text
Guest xem landing/demo
  -> Đăng ký/đăng nhập
  -> Tải CV + dán JD
  -> Xem CV/JD analysis
  -> Bắt đầu mock interview cá nhân hoá
  -> Nhận report có evidence + action plan
  -> Luyện lại STAR/case hoặc mua gói khi hết quota
```

Mỗi bước phải trả lời rõ cho user: đang xử lý gì, dữ liệu được lưu ở đâu/mục đích nào, còn bao nhiêu quota và hành động tiếp theo là gì. Không được khoá luồng xem demo bằng login modal trước khi user thực sự tạo/lưu dữ liệu.

## 7. Success metrics và guardrails

| Nhóm | Metric đề xuất | Guardrail |
| --- | --- | --- |
| Activation | % user hoàn tất interview đầu tiên trong 24h | Không thúc ép upload CV trước khi giải thích lợi ích. |
| Learning loop | % user xem report rồi mở một practice tiếp theo trong 7 ngày | Feedback không chỉ có score; phải có action item. |
| Reliability | % interview/report hoàn tất không cần support | Void quota khi terminal failure xảy ra trước first-question persistence và session activation; sau activation áp dụng BR-08. |
| Revenue | Checkout paid / checkout started | Không kích hoạt plan trước webhook verified. |
| Trust | Tỉ lệ xóa dữ liệu/support privacy complaint | Audio/video opt-in, retention rõ ràng. |

## 8. Release slices

| Slice | User value | Scope |
| --- | --- | --- |
| Release A — Foundation | Có account và dữ liệu bền vững | Auth, profile, database, migration, dashboard skeleton. |
| Release B — Core practice | Luyện và nhận feedback | Resume/JD text, mock interview text, report; STAR/scenario theo priority Should và capacity. |
| Release C — Monetisation | Mua gói an toàn | Plans/quota, checkout sandbox, webhook/idempotency, order history. |
| Release D — Hardening | Sẵn sàng production | Monitoring, privacy/delete, test suite, backup/restore, legal pages. |
