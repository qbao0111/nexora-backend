# 00 — Tóm tắt điều hành

**Ứng dụng**: Nexora AI (`com.nexora.app`) — nền tảng luyện phỏng vấn bằng AI
**Commit audit**: `c59e138` · branch `main` · 160 file TypeScript trong `src/`
**Stack**: Expo SDK 57.0.23, React Native 0.86.3, React 19.2.3, expo-router 57, TanStack Query 5, axios 1.20
**Backend**: `https://nexora-backend-q32b.onrender.com/api/v1` (ASP.NET Core)

---

## 1. Kết luận

> ❌ **Ứng dụng KHÔNG thể release lên Google Play ở trạng thái hiện tại.**

Có **8 vấn đề P0 (blocker)** trong đó **3 vấn đề có nguy cơ khiến tài khoản developer bị đình chỉ**, không chỉ reject bản build:

- Bán nội dung số bằng cổng thanh toán ngoài (vi phạm **Payments policy** — Google xử lý rất nặng).
- Khai báo quyền `RECORD_AUDIO` nhưng không hề dùng trên Android (vi phạm **Permissions & APIs that Access Sensitive Information**).
- Chính sách quyền riêng tư trong app mô tả các tính năng **không tồn tại** (vi phạm **Misrepresentation / Deceptive Behavior**).

Ngoài ra tính năng cốt lõi nhất của sản phẩm — **phỏng vấn bằng giọng nói** — hoàn toàn **không hoạt động trên Android**, vì toàn bộ lớp speech/TTS được viết bằng Web API (`window.SpeechRecognition`, `SpeechSynthesisUtterance`, `new Audio()`, `URL.createObjectURL`). Reviewer sẽ gặp app "chết tính năng chính" ngay lần thử đầu.

---

## 2. Bảng blocker P0 (bắt buộc fix trước khi submit)

| ID | Vấn đề | Chính sách bị vi phạm | Bằng chứng | Effort |
|---|---|---|---|---|
| **P0-01** | Thanh toán gói PRO mở URL cổng ngoài bằng `Linking.openURL()` thay vì Google Play Billing | Payments | `src/app/(app)/pricing/index.tsx:91` | 5–8 ngày |
| **P0-02** | `RECORD_AUDIO` khai báo trong `app.json` nhưng không có code native nào yêu cầu / dùng quyền; bài test mic **giả lập thành công** bằng `setTimeout` | Permissions & APIs that Access Sensitive Information | `app.json:22-26`, `src/app/(app)/interview/preflight.tsx:711-716` | 3–5 ngày |
| **P0-03** | Speech-to-text & TTS chỉ chạy trên web → tính năng phỏng vấn giọng nói không hoạt động trên Android | Spam, Minimum Functionality & Broken Functionality | `src/services/speech.ts:23-39`, `src/services/tts.ts:107-184` | 5–8 ngày |
| **P0-04** | Không có cơ chế **báo cáo / flag nội dung AI** ngay trong app | AI-Generated Content | toàn bộ `src/app/(app)/interview/`, `src/components/interview/` | 2–3 ngày |
| **P0-05** | Chức năng "Xuất dữ liệu cá nhân" **không xuất gì** — gọi API rồi bỏ kết quả, chỉ hiện Alert giả | Misrepresentation + User Data (data portability) | `src/app/(app)/account/index.tsx:156-169` | 1 ngày |
| **P0-06** | Hai nút quản lý ảnh đại diện là **stub Alert**, không làm gì | Broken Functionality | `src/app/(app)/account/index.tsx:252,260` | 1–2 ngày |
| **P0-07** | Xóa tài khoản chỉ là "gửi yêu cầu"; **chưa có URL xóa tài khoản trên web** để khai báo Play Console | App account deletion requirements | `src/api/user.api.ts:38-40` | 2 ngày (cần backend + web) |
| **P0-08** | Chính sách trong app mô tả sai thực tế: nói dùng "Google Play Billing", nói có "xuất JSON", ngày cập nhật ghi `25.09.2026`; link `https://nexora.vn/privacy` chưa xác minh sống | Misrepresentation + Play Console requirement (privacy policy URL) | `src/components/ui/legal-policy-modal.tsx:253,344,438,486` | 1–2 ngày |

---

## 3. Tổng hợp theo mức độ

| Mức | Số lượng | Chủ đề chính |
|---|---|---|
| **P0 — Blocker** | 8 | Payments, Permissions, Broken functionality, AI content reporting, Data deletion/portability, Misrepresentation |
| **P1 — High** | 14 | Rò rỉ log production, không bật R8/minify, refresh-token flow sai, thiếu auth guard trên route group `(app)`, token trong `localStorage` (web), không giới hạn file upload, cache PII không dọn, Azure speech token không invalidate khi logout, `Math.random()` làm idempotency key, không có crash reporting, 14 CVE moderate trong dependency |
| **P2 — Medium** | 13 | SignalR khai báo nhưng chưa cài package (realtime chết → polling 3s), dependency chết, file rác 600KB trong repo, thiếu test runner, chưa verify 16 KB page size, thiếu `blockedPermissions` |
| **P3 — Low** | 6 | Icon 799KB, `INTERNET` khai báo thừa, `versionCode` trùng lặp với `appVersionSource: remote`, chưa có `expo-updates` policy |

Chi tiết từng finding: [`01-SECURITY-AUDIT.md`](./01-SECURITY-AUDIT.md)

---

## 4. Điểm tích cực đã làm đúng (giữ nguyên, đừng phá)

Audit không tìm thấy các lỗi kinh điển sau — đây là nền tảng tốt:

- ✅ **Không có secret hardcode**: grep toàn bộ `src/` theo pattern API key / token / password literal → 0 kết quả.
- ✅ **Không có `eval`, `new Function`, `WebView`, `dangerouslySetInnerHTML`** → không có sink RCE/XSS trong native.
- ✅ **`.gitignore` che `.env*`, `*.jks`, `*.p8`, `*.p12`, `*.key`, `*.mobileprovision`, `*.pem`** và không có file secret nào bị track.
- ✅ **Access token lưu trong `expo-secure-store`** trên native (Keystore/Keychain), không dùng AsyncStorage.
- ✅ **Không có HTTP cleartext** — chỉ 1 chuỗi `http://` và đó là XML namespace của SSML.
- ✅ **401 refresh có single-flight lock + queue** (`src/api/client.ts:57-73`) — chống refresh storm, thiết kế đúng.
- ✅ **SSML được escape XML** trước khi gửi Azure TTS (`src/services/tts.ts:5-22`) → chống SSML injection.
- ✅ **`queryClient.clear()` khi login/logout/auth-error** → không rò dữ liệu giữa hai tài khoản trên cùng thiết bị.
- ✅ **Validate password client-side đủ mạnh** (8+, chữ hoa, chữ thường, số, ký tự đặc biệt) — `src/app/(auth)/login.tsx:157-164`.
- ✅ **`secureTextEntry` + `autoComplete` + `autoCapitalize="none"`** đúng trên mọi field mật khẩu/email.
- ✅ **Có Idempotency-Key cho mutation thanh toán** (`src/api/pricing.api.ts:11-19`) → chống double-charge.
- ✅ **Đã có luồng xóa tài khoản trong app** (dù chưa hoàn chỉnh) và tab "Xóa dữ liệu" trong modal pháp lý — đi đúng hướng.
- ✅ **Có escape `encodeURIComponent` cho path param** khi gọi speech token API (`src/services/speechApi.ts:18`).

---

## 5. Ước lượng effort & timeline

| Phase | Nội dung | Effort (người-ngày) | Có thể song song? |
|---|---|---|---|
| **Phase 0** | Chuẩn bị: tách môi trường, dựng dev build, bật build-properties | 2 | — |
| **Phase 1** | Fix P0 nhóm "Broken functionality" (P0-03, P0-02, P0-05, P0-06) | 10–14 | Một phần |
| **Phase 2** | Fix P0 nhóm "Payments" (P0-01) — chuyển sang Google Play Billing | 6–9 | Có (cần backend) |
| **Phase 3** | Fix P0 nhóm "Pháp lý & dữ liệu" (P0-04, P0-07, P0-08) | 5–7 | Có |
| **Phase 4** | Fix toàn bộ P1 (bảo mật) | 8–11 | Có |
| **Phase 5** | Hardening build + P2 + kiểm thử + khai báo Play Console | 5–7 | — |
| | **Tổng** | **36–50 người-ngày** | |

**Timeline thực tế đề xuất**: 5–7 tuần với 2 dev mobile + 1 dev backend (Play Billing verification bắt buộc phải làm ở backend).

**Đường tắt nếu cần ra bản đầu sớm (Closed Testing)**: xem "Phụ lục B — Chiến lược MVP thu hẹp" trong [`03-REMEDIATION-PLAN.md`](./03-REMEDIATION-PLAN.md) — cắt tính năng thanh toán và voice ra khỏi bản v1 để chỉ còn 3 blocker.

---

## 6. Rủi ro cần cảnh báo cho stakeholder

| Rủi ro | Mức | Ghi chú |
|---|---|---|
| **Đình chỉ tài khoản developer vì Payments policy** | Cao | Google không chỉ reject build mà có thể terminate account với vi phạm thanh toán. Đừng submit thử "xem sao". |
| **Phí Google Play 15–30%** trên mọi giao dịch gói PRO | Cao | Tác động trực tiếp mô hình doanh thu — cần quyết định business trước khi code. |
| **Backend chưa có endpoint verify Play purchase token** | Cao | Là đường găng (critical path). Phải kick-off backend song song ngay. |
| **Không có crash reporting** → không biết app crash trên máy thật | Trung bình | Play Vitals xấu → bị giảm hiển thị trên Store. |
| **Backend trên Render free tier** (`onrender.com`) — cold start có thể 30s+ | Trung bình | Timeout axios là 30s (`src/api/client.ts:17`). Reviewer gặp app treo → nghi broken functionality. |
| **14 CVE moderate** trong dependency tree | Thấp–TB | Không chặn release nhưng sẽ bị nêu trong pre-launch report / audit nội bộ. |
