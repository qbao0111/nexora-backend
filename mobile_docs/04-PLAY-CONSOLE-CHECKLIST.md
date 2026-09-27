# 04 — Checklist khai báo Google Play Console

> Hoàn thành **toàn bộ** checklist này trước khi bấm Submit. Mỗi ô phải có người tick và ngày.
>
> ⚠️ **Nguyên tắc số 1**: mọi câu trả lời trong Data Safety phải được **đối chiếu với code thực tế**, không khai theo cảm tính. Khai sai Data Safety là một trong những lý do bị reject/suspend phổ biến nhất, và Google có thể phát hiện qua phân tích tự động app binary.

---

## A. Thông tin ứng dụng (App content)

| # | Hạng mục | Giá trị cho Nexora | ✓ |
|---|---|---|---|
| A1 | App name | Nexora AI | ☐ |
| A2 | Package name | `com.nexora.app` | ☐ |
| A3 | Short description (≤80 ký tự) | Không được nhắc tính năng đã bị cắt (voice nếu chọn Đường B) | ☐ |
| A4 | Full description | Phải khớp chính xác tính năng thật. Nêu rõ có nội dung do AI tạo | ☐ |
| A5 | Screenshots | **Chỉ chụp tính năng hoạt động thật**. Không dùng mockup/ảnh dựng | ☐ |
| A6 | Feature graphic | — | ☐ |
| A7 | App category | Education hoặc Business (không phải Tools) | ☐ |
| A8 | Contact email | Email thật, được theo dõi (không phải noreply) | ☐ |
| A9 | Website | `https://nexora.vn` — phải sống | ☐ |

---

## B. Privacy policy

| # | Yêu cầu | Trạng thái cần đạt | ✓ |
|---|---|---|---|
| B1 | Privacy policy URL khai trên Console | `https://nexora.vn/privacy` — **verify trả HTTP 200 từ mạng ngoài, chế độ ẩn danh** | ☐ |
| B2 | Nội dung web khớp nội dung trong app | Đối chiếu với `src/components/ui/legal-policy-modal.tsx` sau Bước 3.3 | ☐ |
| B3 | Chính sách nêu tên app "Nexora AI" | — | ☐ |
| B4 | Liệt kê **toàn bộ** bên thứ ba nhận dữ liệu | Xem bảng C5 bên dưới | ☐ |
| B5 | Nêu rõ quyền của user (truy cập, sửa, xóa, xuất) | — | ☐ |
| B6 | Nêu rõ dữ liệu giữ lại sau khi xóa tài khoản + lý do | Bắt buộc bởi account deletion policy | ☐ |
| B7 | Ngày cập nhật đúng và hợp lệ | Sửa `legal-policy-modal.tsx:507` (đang là "25.09.2026") | ☐ |
| B8 | Không có paywall/login chặn truy cập chính sách | — | ☐ |

---

## C. Data Safety form

> Đây là phần dài nhất và dễ khai sai nhất. Bảng dưới được soạn theo **dữ liệu Nexora thực sự xử lý**, căn cứ vào code.

### C1. Tổng quan

| Câu hỏi | Trả lời cho Nexora | Căn cứ trong code | ✓ |
|---|---|---|---|
| App có thu thập hoặc chia sẻ dữ liệu người dùng bắt buộc? | **Có** | Toàn bộ `src/api/` | ☐ |
| Toàn bộ dữ liệu người dùng có được mã hóa khi truyền? | **Có** | HTTPS-only; grep `http://` → 0 (chỉ XML namespace) | ☐ |
| App có cung cấp cách để user yêu cầu xóa dữ liệu? | **Có** | `src/api/user.api.ts:38-40` + web URL (Bước 3.2) | ☐ |
| App có tuân theo Families policy? | **Không** — target audience 18+ | Xem F3 | ☐ |

### C2. Personal info

| Loại dữ liệu | Thu thập? | Chia sẻ? | Bắt buộc? | Mục đích | Căn cứ code | ✓ |
|---|---|---|---|---|---|---|
| Email address | ✅ Có | Xem C5 | Bắt buộc | Account management | `RegisterRequest`, `LoginRequest` (`src/api/types/auth.types.ts`) | ☐ |
| Name (displayName) | ✅ Có | Xem C5 | Bắt buộc | Account management, App functionality | `UpdateProfileRequest.displayName` (`src/api/user.api.ts:5`) | ☐ |
| User IDs | ✅ Có | Xem C5 | Bắt buộc | Account management | `UserDto.id` | ☐ |
| Address / Phone | ⚠️ **Có thể** | — | — | ⚠️ **CV có thể chứa số điện thoại/địa chỉ** — phải khai nếu backend parse ra field riêng | `src/api/resumes.api.ts` | ☐ |
| Other info (CV content) | ✅ Có | Xem C5 | Bắt buộc | App functionality | `resumesApi.presign/finalize` | ☐ |

> ⚠️ **Quyết định cần làm với backend team**: CV được parse thành structured data (tên, email, SĐT, kinh nghiệm) hay chỉ lưu file? Nếu parse → phải khai thêm các loại Personal info tương ứng.

### C3. Financial info

| Loại | Thu thập? | Ghi chú | ✓ |
|---|---|---|---|
| Purchase history | ✅ Có | `OrderItemResponse` (`src/api/types/billing.types.ts:84-91`) — lịch sử đơn hàng | ☐ |
| Payment info (card, CVV) | ❌ Không | Sau Phase 2, Google Play xử lý toàn bộ. Chính sách hiện tại khẳng định đúng điều này (`legal-policy-modal.tsx:438`) | ☐ |

### C4. Audio / Files

| Loại | Thu thập? | Điều kiện | Căn cứ | ✓ |
|---|---|---|---|---|
| Voice or sound recordings | ⚠️ **Phụ thuộc Đường A/B** | **Đường A** → ✅ Có, chia sẻ với Microsoft Azure. **Đường B** → ❌ Không (đã cắt voice) | `src/services/tts.ts:121`, `speechApi.ts` | ☐ |
| Files and docs | ✅ Có | CV (PDF/DOCX) upload | `src/app/(app)/resumes/index.tsx:130` | ☐ |
| Photos | ⚠️ Chỉ nếu Bước 1.6 chọn Lựa chọn A (avatar) | — | — | ☐ |

### C5. Bên thứ ba nhận dữ liệu — PHẢI XÁC MINH VỚI BACKEND TEAM

| Bên thứ ba | Nhận dữ liệu gì | Đã công bố trong chính sách? | Đã khai Data Safety? | ✓ |
|---|---|---|---|---|
| **Microsoft Azure Speech Services** | Giọng nói / text để TTS-STT (chỉ nếu Đường A) | ❌ **Chưa** — phải thêm | ☐ | ☐ |
| **Nhà cung cấp LLM** (OpenAI? Azure OpenAI? Gemini? — ❓ **cần xác nhận**) | Nội dung CV, JD, câu trả lời phỏng vấn | ❌ **Chưa** — phải thêm | ☐ | ☐ |
| **Render** (hosting backend) | Toàn bộ dữ liệu (data processor) | ❌ Chưa | ☐ | ☐ |
| **AWS S3 / R2** (lưu CV) | File CV | ❌ Chưa | ☐ | ☐ |
| **Sentry** (sau Bước 4.11) | Crash data đã scrub | ❌ Chưa | ☐ | ☐ |
| **Google Play Billing** | Purchase token, order info | Sẽ công bố sau Phase 2 | ☐ | ☐ |

> 🔴 **Đây là khoảng trống nghiêm trọng nhất trong Data Safety hiện tại.** Chính sách trong app chỉ nói "không bán dữ liệu cho bên thứ ba vì mục đích quảng cáo" (`legal-policy-modal.tsx:340`) — nhưng việc **gửi CV và câu trả lời tới một LLM provider** là chia sẻ dữ liệu với bên thứ ba và **bắt buộc phải công bố**. Policy nói rõ User Data requirements áp dụng cả với third-party AI integrations.

### C6. Data deletion (mục riêng trong Data Safety)

| # | Câu hỏi | Trả lời | ✓ |
|---|---|---|---|
| C6.1 | App có cho phép user yêu cầu xóa dữ liệu? | **Có** | ☐ |
| C6.2 | URL yêu cầu xóa tài khoản | `https://nexora.vn/xoa-tai-khoan` — phải sống (Bước 3.2a) | ☐ |
| C6.3 | Có in-app path để xóa? | **Có** — Cài đặt tài khoản → Khu vực nguy hiểm | ☐ |
| C6.4 | Có dữ liệu nào bị giữ lại sau khi xóa? | **Có** — hóa đơn/giao dịch, log chống gian lận. **Phải liệt kê cụ thể** | ☐ |

---

## D. AI-Generated Content declaration

| # | Hạng mục | Việc phải làm | ✓ |
|---|---|---|---|
| D1 | Khai app có chứa nội dung do AI tạo | **Có** — không được che giấu | ☐ |
| D2 | Có cơ chế báo cáo nội dung AI trong app | Hoàn thành Bước 3.1 — gắn ở **9 surface** | ☐ |
| D3 | Mô tả biện pháp chống sinh nội dung vi phạm | Cần tài liệu: system prompt guardrails, content filter của LLM provider, moderation pipeline | ☐ |
| D4 | Quy trình xử lý report của user | `docs/moderation-process.md` (Bước 3.1) | ☐ |
| D5 | Nhãn "Nội dung do AI tạo" hiển thị trong app | Bước 3.1 mục 4 | ☐ |
| D6 | Store listing nêu rõ đây là công cụ AI | Trong full description | ☐ |

> Điều khoản cần đáp ứng (nguyên văn): *"Apps that generate content using AI must contain in-app user reporting or flagging features that allow users to report or flag offensive content to developers without needing to exit the app."*

---

## E. Payments & Monetization

| # | Hạng mục | Trạng thái cần đạt | ✓ |
|---|---|---|---|
| E1 | Khai app có in-app purchases | **Có** (sau Phase 2) | ☐ |
| E2 | Tạo product/subscription trên Console | Mỗi `planPriceId` có product tương ứng (Bước 2.2) | ☐ |
| E3 | Giá trong app khớp giá Google hiển thị | Bắt buộc: *"In-app pricing must match the pricing displayed in the user-facing Play billing interface"* | ☐ |
| E4 | **Không còn** luồng thanh toán ngoài trong app Android | `grep -rn "Linking.openURL" src/app/\(app\)/pricing/` → 0 | ☐ |
| E5 | Backend verify purchase token qua Play Developer API | Bước 2.3 | ☐ |
| E6 | Acknowledge purchase trong 3 ngày | Bước 2.4 — nếu không Google tự hoàn tiền | ☐ |
| E7 | Có nút quản lý/hủy subscription (nếu là subscription) | Dẫn tới trang Google Play subscriptions | ☐ |
| E8 | Chính sách hoàn tiền khớp Google Play | Sửa `legal-policy-modal.tsx:456-463` (Bước 2.5) | ☐ |
| E9 | Đăng ký tax & payment profile trên Console | — | ☐ |
| E10 | Thêm license tester để test không mất tiền | Console → Setup → License testing | ☐ |

---

## F. Content rating & Target audience

| # | Hạng mục | Giá trị cho Nexora | Lưu ý | ✓ |
|---|---|---|---|---|
| F1 | Hoàn thành Content Rating questionnaire | — | Câu trả lời phải khớp chính sách trong app | ☐ |
| F2 | Có user-generated content? | **Có** — câu trả lời phỏng vấn, feedback công khai (`allowPublicDisplay`) | Cần nêu có moderation | ☐ |
| F3 | Target age group | **18+** | ⚠️ Phải sửa `legal-policy-modal.tsx:359` bỏ mệnh đề "13 tuổi" (xem P2-13) | ☐ |
| F4 | Có bạo lực / tình dục / ma túy / cờ bạc? | **Không** | — | ☐ |
| F5 | Có mua hàng trong app? | **Có** | — | ☐ |
| F6 | App có target trẻ em? | **Không** | Nếu "Có" → kích hoạt Families policy, rất khắt khe | ☐ |
| F7 | Ads declaration | **Không có quảng cáo** | Verify: 0 SDK ads trong `package.json` | ☐ |

---

## G. Permissions declaration

| # | Việc làm | Kết quả cần đạt | ✓ |
|---|---|---|---|
| G1 | Liệt kê toàn bộ `uses-permission` trong `AndroidManifest.xml` sau prebuild | Lưu vào `evidence/final-permissions.md` (Bước 5.6) | ☐ |
| G2 | Mỗi quyền có lý do sử dụng rõ ràng | Không có quyền nào "để dành" | ☐ |
| G3 | `RECORD_AUDIO` đã bị xóa (nếu chọn Đường B) | `grep RECORD_AUDIO android/app/src/main/AndroidManifest.xml` → 0 | ☐ |
| G4 | `MODIFY_AUDIO_SETTINGS` đã bị xóa (nếu chọn Đường B) | — | ☐ |
| G5 | Không có quyền nhạy cảm đặc biệt | Không `QUERY_ALL_PACKAGES`, không All files access, không SMS/Call Log, không background location | ☐ |
| G6 | `blockedPermissions` đã khai | Bước 5.6 | ☐ |
| G7 | Không có quyền lạ do lib tự thêm | Đối chiếu manifest trước/sau khi thêm dependency | ☐ |

---

## H. Technical requirements

| # | Yêu cầu | Cách verify | ✓ |
|---|---|---|---|
| H1 | Target API 36 (Android 16) | `aapt2 dump badging <aab>` → `targetSdkVersion='36'` | ☐ |
| H2 | 16 KB page size support | `check_elf_alignment.sh` trên mọi `.so` → ALIGNED (Bước 5.2) | ☐ |
| H3 | AAB format (không APK) | `eas.json` production đã có `"buildType": "app-bundle"` ✅ | ☐ |
| H4 | App signing by Google Play đã bật | Console → Setup → App signing | ☐ |
| H5 | Bản build là release + minify | Bước 5.1 | ☐ |
| H6 | Kích thước download hợp lý | Sau khi nén asset (Bước 5.3) | ☐ |
| H7 | Không crash khi mở lần đầu | Test trên 3 thiết bị | ☐ |
| H8 | Hỗ trợ cả 32-bit và 64-bit (hoặc chỉ 64-bit) | Kiểm `lib/` trong AAB | ☐ |

> Yêu cầu target API hiện hành (nguyên văn): *"Starting August 31, 2026, new apps and app updates must target Android 16 (API level 36) or higher to be submitted to Google Play."* Có thể xin gia hạn tới **01-11-2026**.

---

## I. Testing tracks (làm theo đúng thứ tự)

| # | Bước | Mục đích | ✓ |
|---|---|---|---|
| I1 | **Internal testing** (≤100 tester) | Verify build chạy được, IAP hoạt động với license tester | ☐ |
| I2 | Chạy đầy đủ [`05-TEST-PLAN.md`](./05-TEST-PLAN.md) trên bản Internal | Bắt lỗi release-only (R8/minify) | ☐ |
| I3 | Đọc **Pre-launch report** trên Console | Google tự test trên nhiều thiết bị — xem crash, ANR, cảnh báo bảo mật, accessibility | ☐ |
| I4 | Sửa mọi vấn đề Pre-launch report nêu | — | ☐ |
| I5 | **Closed testing** (theo yêu cầu hiện hành với tài khoản developer cá nhân mới) | Google có thể yêu cầu closed test với số tester và thời lượng nhất định trước khi cho production — **kiểm tra yêu cầu hiện hành áp dụng cho tài khoản của bạn** | ☐ |
| I6 | Thu thập feedback + sửa | — | ☐ |
| I7 | **Production** | — | ☐ |

> ⚠️ Nếu tài khoản developer là **cá nhân đăng ký sau tháng 11/2023**, Google yêu cầu closed testing với tối thiểu số tester và thời lượng nhất định trước khi được phát hành production. Yêu cầu này thay đổi theo thời gian — **xác nhận trên Console của bạn**, đừng dựa vào con số nhớ được.

---

## J. Cổng chặn cuối cùng — tick hết mới được submit

| # | Điều kiện | ✓ |
|---|---|---|
| J1 | 8/8 P0 đã đóng, mỗi P0 có bằng chứng trong `docs/release-audit/evidence/` | ☐ |
| J2 | 14/14 P1 đã đóng hoặc có accept-risk được phê duyệt **bằng văn bản** | ☐ |
| J3 | Mục A → I của checklist này tick 100% | ☐ |
| J4 | [`05-TEST-PLAN.md`](./05-TEST-PLAN.md) pass trên ≥3 thiết bị Android (13/14/15) | ☐ |
| J5 | 3 URL (`/privacy`, `/terms`, `/xoa-tai-khoan`) trả 200 từ mạng ngoài, chế độ ẩn danh | ☐ |
| J6 | Data Safety đã **đối chiếu với code**, không khai theo cảm tính | ☐ |
| J7 | Không còn nút/tính năng nào không hoạt động trong app | ☐ |
| J8 | Crash reporting hoạt động, xác nhận nhận được event từ bản production | ☐ |
| J9 | Pre-launch report không còn lỗi mức High | ☐ |
| J10 | Có người ngoài team (không phải dev viết code) dùng thử toàn bộ app và xác nhận không gặp tính năng hỏng | ☐ |

---

## K. Sau khi submit — theo dõi

| # | Việc | Tần suất | ✓ |
|---|---|---|---|
| K1 | Theo dõi Policy status trên Console | Hàng ngày trong tuần đầu | ☐ |
| K2 | Theo dõi Android Vitals (crash rate, ANR rate) | Hàng ngày trong tuần đầu | ☐ |
| K3 | Theo dõi Sentry | Hàng ngày | ☐ |
| K4 | Theo dõi user review để phát hiện tính năng hỏng chưa biết | Hàng ngày | ☐ |
| K5 | Theo dõi báo cáo nội dung AI từ user + xử lý theo SLA | Theo `docs/moderation-process.md` | ☐ |
| K6 | Chuẩn bị sẵn nội dung appeal nếu bị reject | Dùng [`02-PLAY-POLICY-COMPLIANCE.md`](./02-PLAY-POLICY-COMPLIANCE.md) làm bằng chứng đã tuân thủ | ☐ |
