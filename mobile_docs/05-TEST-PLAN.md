# 05 — Kế hoạch kiểm thử xác nhận (verification test plan)

> Chạy **toàn bộ** tài liệu này sau khi hoàn thành Phase 1–5 của [`03-REMEDIATION-PLAN.md`](./03-REMEDIATION-PLAN.md), trên **bản build release + minify** (không phải debug).
>
> Mọi test case fail = không submit.

---

## 0. Điều kiện tiên quyết

| # | Điều kiện | ✓ |
|---|---|---|
| 0.1 | Build là **release profile** với `enableMinifyInReleaseBuilds: true` — R8 là nguyên nhân số 1 của lỗi "chỉ xảy ra ở production" | ☐ |
| 0.2 | Backend trỏ **staging** (để test thanh toán/xóa tài khoản không phá dữ liệu thật) | ☐ |
| 0.3 | Có tối thiểu 3 thiết bị theo ma trận mục 1 | ☐ |
| 0.4 | Có `adb` hoạt động để chạy test deep link và đọc logcat | ☐ |
| 0.5 | Có license tester email đã thêm vào Play Console (cho test IAP) | ☐ |
| 0.6 | Có tài khoản test sạch (chưa từng dùng app) và tài khoản test đã có dữ liệu | ☐ |

---

## 1. Ma trận thiết bị

| Thiết bị | Android | Vai trò | Bắt buộc |
|---|---|---|---|
| Máy thật, RAM ≥6GB | **15** | Test target API + 16 KB page size | ✅ |
| Máy thật, RAM ≤4GB | **13 hoặc 14** | Test hiệu năng máy yếu, OOM khi upload CV | ✅ |
| Emulator Android 15, **16 KB page size system image** | 15 | Verify P2-04 | ✅ |
| Máy thật màn hình nhỏ (≤5.5") | bất kỳ | Test layout tab arch tùy chỉnh (`(tabs)/_layout.tsx`) | Khuyến nghị |
| Tablet | bất kỳ | Test responsive (dùng `useWindowDimensions`) | Khuyến nghị |

Ghi kết quả vào `docs/release-audit/evidence/device-matrix.md`.

---

## 2. Test bảo mật (SEC)

### SEC-01 · Không rò rỉ token / PII vào logcat (đóng P1-01)

**Cách làm**
1. Build release, cài lên máy thật.
2. `adb logcat -c` để xóa log cũ.
3. Thực hiện: login → upload CV → làm 1 phiên phỏng vấn → xem báo cáo → gây lỗi mạng (bật airplane mode giữa request) → gây lỗi 500 (nếu staging có endpoint test).
4. Dump log: `adb logcat -d > evidence/P1-01-logcat.txt`
5. Grep các pattern:
   ```
   grep -iE "Bearer |authorization|refresh_token|nexora_access|@gmail|@yahoo|password" evidence/P1-01-logcat.txt
   ```

**Pass**: 0 kết quả.
**Fail nếu**: thấy bất kỳ token, email, hay nội dung CV.

☐ Pass ☐ Fail

---

### SEC-02 · Deep link không bypass được xác thực (đóng P1-04)

**Cách làm**: đăng xuất hoàn toàn (hoặc xóa data app), rồi chạy từng lệnh:

```
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/account" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/pricing" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/resumes" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/career-profile" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/career-goals" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/cv-analysis/fake-id" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/interview/fake-id" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/interview/report/fake-id" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/growth/progress-dashboard" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/star-builder" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/scenarios/fake-id" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(tabs)/home" com.nexora.app
```

**Pass**: mọi lệnh đều dẫn tới màn hình login, không crash, không bắn toast lỗi 401, không hiện UI có skeleton/dữ liệu rác.

☐ Pass ☐ Fail

---

### SEC-03 · Deep link với ID không hợp lệ không gây crash

**Cách làm**: đăng nhập rồi chạy với ID độc hại:

```
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/interview/../../etc/passwd" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/cv-analysis/%3Cscript%3E" com.nexora.app
adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/scenarios/00000000-0000-0000-0000-000000000000" com.nexora.app
```

**Pass**: app hiện thông báo lỗi thân thiện hoặc empty state, **không crash**, không hiện message lỗi thô từ backend.

☐ Pass ☐ Fail

---

### SEC-04 · IDOR — không xem được dữ liệu của user khác

**Cách làm**
1. Đăng nhập tài khoản A, tạo 1 phiên phỏng vấn, ghi lại `interviewId`.
2. Đăng xuất, đăng nhập tài khoản B.
3. Deep link tới `nexoramobile://(app)/interview/report/<interviewId-của-A>`.

**Pass**: hiện lỗi 403/404 thân thiện, **không** thấy dữ liệu của A.
**Lưu ý**: đây là test kiểm chứng backend authorization — nếu fail, đây là lỗ hổng **Critical** cần fix ở backend ngay.

☐ Pass ☐ Fail

---

### SEC-05 · Dữ liệu không rò rỉ giữa hai tài khoản trên cùng thiết bị

**Cách làm**
1. Đăng nhập A, xem danh sách CV, báo cáo, gói dịch vụ.
2. Đăng xuất (không kill app).
3. Đăng nhập B ngay lập tức.
4. Kiểm mọi màn hình: CV, career profile, interview history, pricing, growth dashboard.

**Pass**: không thấy bất kỳ dữ liệu nào của A. (`queryClient.clear()` đã được gọi ở cả login và logout — verify nó hoạt động thật.)

☐ Pass ☐ Fail

---

### SEC-06 · Session bền vững, không bị logout bất ngờ (đóng P1-03)

**Cách làm**
1. Login trên máy thật.
2. Force stop app (`adb shell am force-stop com.nexora.app`).
3. Mở lại → **phải vẫn đăng nhập**.
4. Lặp lại sau 1 giờ, sau 24 giờ, sau 7 ngày.
5. Test riêng: bật airplane mode → mở app → phải hiện lỗi mạng, **không** tự logout.
6. Test riêng: reboot thiết bị → mở app → vẫn đăng nhập.

**Pass**: không lần nào bị logout ngoài ý muốn.
**Ghi kết quả**: `evidence/P1-03-session-persistence.md` với timestamp từng lần test.

☐ Pass ☐ Fail

---

### SEC-07 · Refresh token rotation & revoke

**Cách làm**
1. Login, bắt refresh token hiện tại (từ log dev build).
2. Trigger refresh (chờ access token hết hạn hoặc gọi API sau khi xóa access token).
3. Thử dùng lại refresh token **cũ** (đã bị rotate) bằng curl trực tiếp tới backend.

**Pass**: refresh token cũ bị từ chối 401. Nếu backend có reuse detection → cả family bị revoke.

☐ Pass ☐ Fail

---

### SEC-08 · Không lộ chi tiết lỗi backend ra UI (đóng P1-07)

**Cách làm**: cùng backend team tạo các response lỗi sau và kiểm toast trong app:

| Response backend | Toast phải hiện |
|---|---|
| 500 với message `"SqlException: Invalid column name 'Emial' in table Users"` | Message chung tiếng Việt, **không** có "SqlException"/"Users" |
| 500 với stack trace dài | Message chung |
| 400 với `{"errors": {"Email": ["The Email field is required"]}}` | "Email là bắt buộc." (map đúng) |
| 429 | "Hệ thống đang xử lý quá nhiều yêu cầu…" |
| Timeout | "Không thể kết nối máy chủ…" |

**Pass**: không có toast nào chứa tên bảng/cột/class/đường dẫn file server.

☐ Pass ☐ Fail

---

### SEC-09 · Traffic hoàn toàn HTTPS, không cleartext

**Cách làm**
1. `grep -n "usesCleartextTraffic" android/app/src/main/AndroidManifest.xml` → phải là `"false"`.
2. Chạy app qua proxy (mitmproxy) **không** cài CA lên thiết bị → mọi request phải **thất bại** (chứng tỏ app không tin proxy tùy ý).
3. Nếu đã cấu hình loại user-installed CA (Bước 4.6): cài CA của mitmproxy lên thiết bị → request vẫn phải thất bại.
4. Dùng Wireshark/tcpdump xác nhận không có traffic HTTP plaintext.

**Pass**: 0 request cleartext, MITM không đọc được nội dung.

☐ Pass ☐ Fail

---

### SEC-10 · File PII được dọn khỏi cache (đóng P1-08)

**Cách làm**
1. Upload 3 CV khác nhau.
2. Xuất dữ liệu cá nhân (P0-05).
3. Ghi âm và upload 1 câu trả lời (nếu chọn Đường A).
4. Liệt kê cache: `adb shell run-as com.nexora.app ls -laR /data/data/com.nexora.app/cache`
5. Liệt kê files dir: `adb shell run-as com.nexora.app ls -laR /data/data/com.nexora.app/files`

**Pass**: không còn file `.pdf`, `.docx`, `.json` export, hay `.m4a` nào chứa dữ liệu cá nhân.
**Ghi kết quả**: `evidence/P1-08-cache-cleanup.txt`

☐ Pass ☐ Fail

---

### SEC-11 · Validate upload file (đóng P1-08)

| Test case | Kỳ vọng | ✓ |
|---|---|---|
| Upload PDF 500KB hợp lệ | Thành công | ☐ |
| Upload DOCX hợp lệ | Thành công | ☐ |
| Upload file 50MB | Bị từ chối **trước khi** upload, message rõ ràng | ☐ |
| Upload file 0 byte | Bị từ chối | ☐ |
| Đổi tên `.exe` thành `.pdf` rồi upload | Backend từ chối (kiểm magic bytes) | ☐ |
| Upload khi mất mạng giữa quá trình | Hiện lỗi, không treo, cache được dọn | ☐ |
| Upload cùng lúc 2 file (spam nút) | Không tạo duplicate, không crash | ☐ |

☐ Pass ☐ Fail

---

### SEC-12 · Quyền đúng và tối thiểu (đóng P0-02)

**Cách làm**
1. `adb shell dumpsys package com.nexora.app | grep -A30 "requested permissions"`
2. Đối chiếu với `evidence/final-permissions.md`.
3. Mở Settings → Apps → Nexora AI → Permissions trên thiết bị, chụp ảnh.

**Pass**
- Nếu chọn **Đường B**: không có `RECORD_AUDIO`, không có `MODIFY_AUDIO_SETTINGS`.
- Nếu chọn **Đường A**: có `RECORD_AUDIO`, và app **thực sự hiện dialog xin quyền hệ thống** khi dùng tính năng voice.
- Không có quyền nào ngoài danh sách đã giải thích được.

☐ Pass ☐ Fail

---

### SEC-13 · Crash reporting hoạt động và đã scrub PII (đóng P1-11)

**Cách làm**
1. Trên build production (hoặc release có Sentry DSN staging), trigger một crash cố ý qua debug menu ẩn.
2. Kiểm Sentry dashboard trong 2 phút.
3. Mở chi tiết event, kiểm **toàn bộ** payload.

**Pass**
- Crash xuất hiện trên Sentry.
- Stack trace đã de-minify, đọc được file/dòng.
- **Không** có: email, header `Authorization`, `Cookie`, nội dung CV, nội dung câu trả lời phỏng vấn.
- User chỉ được nhận diện bằng ID đã hash.

☐ Pass ☐ Fail

---

### SEC-14 · Binary đã được obfuscate (đóng P1-02)

**Cách làm**
1. Giải nén AAB, dùng `jadx` hoặc `apktool` decompile.
2. Kiểm tên class/method Java/Kotlin.
3. Kiểm JS bundle có bị minify.

**Pass**: tên class/method đã bị rename thành `a`, `b`, `c`…; JS bundle đã minify; không có source map trong AAB.

☐ Pass ☐ Fail

---

## 3. Test tuân thủ chính sách (POL)

### POL-01 · Không còn luồng thanh toán ngoài (đóng P0-01)

| Test case | Kỳ vọng | ✓ |
|---|---|---|
| Mở màn Pricing, bấm mua gói PRO | Hiện dialog **Google Play Billing** (có logo Google Play) | ☐ |
| Mua bằng license tester | Entitlement được cấp, quota tăng đúng | ☐ |
| Kill app giữa lúc mua | Mở lại → purchase được khôi phục và acknowledge | ☐ |
| Hủy giữa flow mua | Đóng dialog gọn gàng, không báo lỗi đỏ | ☐ |
| Giá trong app vs giá Google hiển thị | **Khớp chính xác** | ☐ |
| Tìm bất kỳ link/button nào dẫn ra web thanh toán | **Không có** | ☐ |
| Nút "Khôi phục giao dịch" | Hoạt động, lấy lại entitlement đã mua | ☐ |
| Nút quản lý/hủy subscription (nếu là sub) | Mở trang Google Play subscriptions | ☐ |
| Code check | `grep -rn "Linking.openURL\|checkout.url" src/app/\(app\)/pricing/` → 0 | ☐ |

☐ Pass ☐ Fail

---

### POL-02 · Không còn tính năng hỏng (đóng P0-03, P0-05, P0-06)

**Cách làm**: mở **từng** màn hình trong bảng dưới, bấm **từng** phần tử tương tác, xác nhận mỗi phần tử tạo ra thay đổi thật.

| Route | Phần tử cần kiểm | ✓ |
|---|---|---|
| `(auth)/login` | Login, Register, Forgot password, toggle hiện mật khẩu | ☐ |
| `(auth)/register` | Submit, validate từng field | ☐ |
| `(auth)/verify-email` | Nhập mã, gửi lại mã | ☐ |
| `(auth)/forgot-password` | Gửi email reset | ☐ |
| `(tabs)/home` | Mọi card, mọi CTA, platform stats | ☐ |
| `(tabs)/interview` | Bắt đầu phỏng vấn, xem lịch sử | ☐ |
| `(tabs)/practice` | Mọi mục | ☐ |
| `(tabs)/cv-jd` | Mọi tab, upload, phân tích | ☐ |
| `(tabs)/growth` | Mọi chart, mọi link | ☐ |
| `(tabs)/profile` | Mọi mục menu, modal pháp lý, feedback | ☐ |
| `(app)/account` | Sửa profile, đổi mật khẩu, **xuất dữ liệu**, **avatar**, xóa tài khoản | ☐ |
| `(app)/resumes` | Upload, đặt CV chính, xóa | ☐ |
| `(app)/cv-analysis/[id]` | Mọi tab kết quả, nút báo cáo nội dung AI | ☐ |
| `(app)/interview/preflight` | Chọn loại, seniority, độ khó, mic test (nếu có) | ☐ |
| `(app)/interview/[id]` | Trả lời, nộp bài, kết thúc sớm, coaching modal | ☐ |
| `(app)/interview/report/[id]` | Chia sẻ, luyện lại, chấm lại, báo cáo nội dung AI | ☐ |
| `(app)/interview/history` | Mở từng item | ☐ |
| `(app)/career-goals` + `/create` | Tạo, sửa, xóa goal | ☐ |
| `(app)/career-profile` | Mọi mục | ☐ |
| `(app)/growth/*` (3 màn) | Mọi phần tử | ☐ |
| `(app)/scenarios` + `/[id]` | Mở, thực hiện scenario | ☐ |
| `(app)/star-builder` | Tạo STAR, lưu | ☐ |
| `(app)/pricing` | Xem POL-01 | ☐ |

**Pass**: 0 phần tử nào khi bấm chỉ hiện Alert mô tả mà không làm gì.
**Ghi kết quả**: `evidence/dead-button-audit.md`

☐ Pass ☐ Fail

---

### POL-03 · Báo cáo nội dung AI hoạt động ở mọi surface (đóng P0-04)

| Surface | Có nút báo cáo? | Gửi được? | Backend nhận đúng contentType/contentId? | ✓ |
|---|---|---|---|---|
| Câu hỏi phỏng vấn | ☐ | ☐ | ☐ | ☐ |
| Coaching modal | ☐ | ☐ | ☐ | ☐ |
| Báo cáo phỏng vấn (tổng) | ☐ | ☐ | ☐ | ☐ |
| Báo cáo — từng nhận xét năng lực | ☐ | ☐ | ☐ | ☐ |
| Kết quả phân tích CV–JD | ☐ | ☐ | ☐ | ☐ |
| Learning path | ☐ | ☐ | ☐ | ☐ |
| Skill profile | ☐ | ☐ | ☐ | ☐ |
| STAR suggestions | ☐ | ☐ | ☐ | ☐ |
| Scenario content | ☐ | ☐ | ☐ | ☐ |

**Thêm**
- [ ] Có nhãn "Nội dung do AI tạo" hiển thị ở các surface trên.
- [ ] User nhận được xác nhận sau khi gửi báo cáo.
- [ ] Gửi báo cáo khi mất mạng → hiện lỗi, cho retry, không mất nội dung đã nhập.

☐ Pass ☐ Fail

---

### POL-04 · Xóa tài khoản hoạt động đúng (đóng P0-07)

| Test case | Kỳ vọng | ✓ |
|---|---|---|
| Tìm đường dẫn xóa trong app | Dễ thấy ở Cài đặt tài khoản | ☐ |
| Dialog xác nhận | **2 bước**, liệt kê dữ liệu sẽ bị xóa | ☐ |
| Xác nhận xóa | User bị logout, hiện mốc thời gian hard-delete | ☐ |
| Kiểm DB (staging) | Record đã soft-delete với `scheduledHardDeleteAt` | ☐ |
| Đăng nhập lại trong grace period | Thấy banner "đang chờ xóa" + nút huỷ | ☐ |
| Huỷ yêu cầu xóa | Tài khoản hoạt động lại bình thường | ☐ |
| Mở `https://nexora.vn/xoa-tai-khoan` ở chế độ ẩn danh | Trang load, nêu tên "Nexora AI", có form | ☐ |
| Gửi yêu cầu xóa qua web | Nhận được xác nhận | ☐ |
| Tab "Xóa dữ liệu" trong app | Liệt kê chính xác dữ liệu giữ lại + lý do | ☐ |

☐ Pass ☐ Fail

---

### POL-05 · Nội dung pháp lý khớp thực tế (đóng P0-08)

**Cách làm**: đọc **từng câu** trong modal pháp lý (4 tab) và đối chiếu với hành vi app. Lập bảng.

| Câu khẳng định | Hành vi app tương ứng | Khớp? | ✓ |
|---|---|---|---|
| "…mã hóa qua ASP.NET Core Identity" | Xác nhận với backend | ☐ | ☐ |
| "truyền qua kết nối mã hóa TLS/HTTPS" | SEC-09 pass | ☐ | ☐ |
| "trích xuất bản sao dữ liệu cá nhân (JSON)" | POL-02 → xuất dữ liệu hoạt động | ☐ | ☐ |
| "yêu cầu xóa toàn bộ… trực tiếp trong ứng dụng" | POL-04 pass | ☐ | ☐ |
| "xóa vĩnh viễn trong vòng 30 ngày" | Xác nhận job hard-delete tồn tại | ☐ | ☐ |
| "quyền truy cập Micro duy nhất cho mục đích thu âm" | SEC-12 — khớp Đường A/B đã chọn | ☐ | ☐ |
| "không bao giờ được ghi âm ngầm hay chạy dưới nền" | `enableBackgroundRecording: false` | ☐ | ☐ |
| "dành cho người dùng từ 18 tuổi" | Khớp target audience trên Console | ☐ | ☐ |
| "Google Play Billing" | POL-01 pass | ☐ | ☐ |
| "KHÔNG trực tiếp thu thập số thẻ, CVV" | Đúng — Google xử lý | ☐ | ☐ |
| Chính sách hoàn tiền | Khớp Google Play refund policy | ☐ | ☐ |
| "không chia sẻ dữ liệu cho bên thứ ba" | ⚠️ Phải đã bổ sung danh sách Azure/LLM provider | ☐ | ☐ |
| Ngày cập nhật | Đúng, hợp lệ | ☐ | ☐ |
| Link `nexora.vn/privacy` | Trả 200 | ☐ | ☐ |

**Ghi kết quả**: `evidence/legal-claims-verification.md`

☐ Pass ☐ Fail

---

## 4. Test chức năng end-to-end (E2E)

### E2E-01 · Onboarding tài khoản mới

1. Cài app sạch → mở lần đầu → splash hiện rồi chuyển login.
2. Register với email thật → nhận email xác thực → nhập mã → verify thành công.
3. Login → vào home.
4. Xem modal pháp lý (4 tab, swipe được).

☐ Pass ☐ Fail

---

### E2E-02 · Phỏng vấn đầy đủ (happy path)

1. Preflight: chọn loại (thử cả 7 loại), seniority, độ khó.
2. Vào phòng phỏng vấn → nhận câu hỏi.
3. Trả lời (text và/hoặc voice tùy Đường A/B).
4. Nhận coaching realtime.
5. Kết thúc → chờ báo cáo (theo dõi polling không quá tốn).
6. Xem báo cáo đầy đủ: điểm tổng, từng năng lực, gợi ý.
7. Chia sẻ báo cáo.
8. Luyện lại một câu.
9. Yêu cầu chấm lại.

☐ Pass ☐ Fail

---

### E2E-03 · CV & phân tích JD

1. Upload CV PDF → thành công.
2. Đặt làm CV chính.
3. Upload CV thứ hai DOCX.
4. Tạo JD mới.
5. Chạy phân tích CV–JD → xem đủ các tab kết quả.
6. Xóa một CV.

☐ Pass ☐ Fail

---

### E2E-04 · Thanh toán & entitlement

Xem POL-01. Thêm:
1. Trước khi mua: xác nhận quota FREE đúng.
2. Sau khi mua: quota PRO đúng, `endsAt` đúng.
3. Dùng hết quota → hiện thông báo giới hạn đúng, dẫn tới upgrade.

☐ Pass ☐ Fail

---

### E2E-05 · Growth & career

1. Tạo career goal.
2. Xem progress dashboard, learning path, skill profile.
3. Dùng STAR builder.
4. Làm một scenario.

☐ Pass ☐ Fail

---

## 5. Test độ bền (ROB)

| # | Test case | Kỳ vọng | ✓ |
|---|---|---|---|
| ROB-01 | Mất mạng giữa mỗi flow chính (login, upload, phỏng vấn, thanh toán) | Hiện lỗi rõ ràng, có retry, không crash, không mất dữ liệu đã nhập | ☐ |
| ROB-02 | Mạng rất chậm (throttle 2G qua `adb shell settings`) | App không treo vô hạn; timeout 30s có thông báo. ⚠️ Kiểm riêng với backend cold start Render — có thể cần tăng timeout | ☐ |
| ROB-03 | Backend trả 500 ở mỗi endpoint | Message chung, không lộ chi tiết (SEC-08) | ☐ |
| ROB-04 | Backend trả 429 | Toast rate-limit đúng | ☐ |
| ROB-05 | Xoay màn hình ở mọi màn (dù app khai `portrait`) | Không crash | ☐ |
| ROB-06 | App vào background 10 phút rồi quay lại | State giữ nguyên, không crash, polling đã dừng | ☐ |
| ROB-07 | Nhấn Back liên tục nhanh từ màn sâu nhất | Không crash, không màn hình trắng. ⚠️ Chú ý `predictiveBackGestureEnabled: false` | ☐ |
| ROB-08 | Spam nút submit (double-tap) ở mọi form | Không tạo duplicate (kiểm Idempotency-Key hoạt động) | ☐ |
| ROB-09 | Phiên phỏng vấn dài 60 phút | Không OOM, không leak (kiểm memory qua Android Studio Profiler) | ☐ |
| ROB-10 | Bộ nhớ thiết bị gần đầy → upload CV | Hiện lỗi rõ ràng, không crash | ☐ |
| ROB-11 | Đổi ngôn ngữ hệ thống sang English | App vẫn hoạt động (UI tiếng Việt là chấp nhận được) | ☐ |
| ROB-12 | Bật font size lớn nhất trong Accessibility | Layout không vỡ, text không bị cắt | ☐ |
| ROB-13 | Dark mode / Light mode (app dùng `userInterfaceStyle: automatic`) | Cả hai đều đọc được, không có text cùng màu nền | ☐ |
| ROB-14 | Chạy app trên emulator **16 KB page size** | Toàn bộ E2E pass (đóng P2-04) | ☐ |

---

## 6. Test hiệu năng (PERF)

| # | Chỉ tiêu | Ngưỡng | Cách đo | ✓ |
|---|---|---|---|---|
| PERF-01 | Cold start | < 3 giây tới màn đầu tiên | `adb shell am start -W` | ☐ |
| PERF-02 | Kích thước download AAB | Càng nhỏ càng tốt; ghi nhận số thật sau Bước 5.3 | Play Console | ☐ |
| PERF-03 | Số request trong 5 phút chờ báo cáo | Giảm ≥60% so với trước Bước 5.5 | Charles/mitmproxy đếm | ☐ |
| PERF-04 | Không có request nào khi app ở background | 0 request | mitmproxy | ☐ |
| PERF-05 | Memory sau 60 phút phỏng vấn | Không tăng tuyến tính (không leak) | Android Studio Profiler | ☐ |
| PERF-06 | Không có ANR trong toàn bộ test | 0 ANR | logcat + Play Vitals | ☐ |
| PERF-07 | Crash-free rate trong Internal testing | 100% trong quá trình test | Sentry | ☐ |

---

## 7. Test tự động (AUTO)

| # | Kiểm tra | Command | Pass khi | ✓ |
|---|---|---|---|---|
| AUTO-01 | TypeScript | `npx tsc --noEmit` | 0 lỗi | ☐ |
| AUTO-02 | Lint | `npm run lint` | 0 error | ☐ |
| AUTO-03 | Unit test | `npm test` | 100% pass, coverage ≥60% cho `src/utils` + `src/services` | ☐ |
| AUTO-04 | Dependency audit | `npm audit --omit=dev --audit-level=high` | 0 vulnerability | ☐ |
| AUTO-05 | Expo doctor | `npx expo-doctor` | 0 issue | ☐ |
| AUTO-06 | Không còn `console.*` ngoài logger | `grep -rn "console\." src/ --include=*.ts --include=*.tsx \| grep -v __DEV__ \| grep -v logger.ts` | 0 kết quả | ☐ |
| AUTO-07 | Không còn `Math.random` trong code bảo mật | `grep -rn "Math.random" src/api/ src/services/` | 0 kết quả | ☐ |
| AUTO-08 | Không còn nút stub | `grep -rn "Alert.alert('Thông báo'" src/` | 0 kết quả | ☐ |
| AUTO-09 | Không còn thanh toán ngoài | `grep -rn "Linking.openURL" src/app/\(app\)/pricing/` | 0 kết quả | ☐ |
| AUTO-10 | Không còn secret hardcode | `grep -rnEi "(api[_-]?key\|secret\|sk-[A-Za-z0-9]{10,}\|AIza[0-9A-Za-z_-]{30,})" src/` | 0 kết quả | ☐ |
| AUTO-11 | Target SDK đúng | `aapt2 dump badging <aab> \| grep targetSdk` | `targetSdkVersion='36'` | ☐ |
| AUTO-12 | 16 KB alignment | `check_elf_alignment.sh` trên mọi `.so` | Toàn bộ ALIGNED | ☐ |
| AUTO-13 | Cleartext bị tắt | `grep usesCleartextTraffic android/app/src/main/AndroidManifest.xml` | `"false"` | ☐ |
| AUTO-14 | Quyền đúng | `grep uses-permission android/app/src/main/AndroidManifest.xml` | Khớp `evidence/final-permissions.md` | ☐ |

---

## 8. Bảng ký xác nhận

| Nhóm test | Tổng case | Pass | Fail | Người test | Ngày | Ghi chú |
|---|---|---|---|---|---|---|
| SEC (bảo mật) | 14 | | | | | |
| POL (chính sách) | 5 | | | | | |
| E2E (chức năng) | 5 | | | | | |
| ROB (độ bền) | 14 | | | | | |
| PERF (hiệu năng) | 7 | | | | | |
| AUTO (tự động) | 14 | | | | | |
| **TỔNG** | **59** | | | | | |

### Cổng chặn cuối

☐ **59/59 test case PASS** trên tối thiểu 3 thiết bị.
☐ Toàn bộ bằng chứng đã lưu trong `docs/release-audit/evidence/`.
☐ Có **người ngoài team phát triển** dùng thử toàn bộ app và xác nhận không gặp tính năng hỏng.
☐ Pre-launch report của Google Play không còn lỗi mức High.

**Chỉ khi cả 4 ô trên được tick → được phép submit lên Google Play.**

Người phê duyệt release: ________________  Ngày: __________
