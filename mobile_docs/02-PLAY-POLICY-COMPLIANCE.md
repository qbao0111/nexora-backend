# 02 — Đối chiếu chính sách Google Play

Tài liệu này đối chiếu từng chính sách Google Play liên quan với trạng thái thực tế của Nexora Mobile tại commit `c59e138`, kèm **trích dẫn nguyên văn** điều khoản để team có thể tự kiểm chứng và dùng khi cần appeal.

**Ký hiệu**: ✅ Đạt · ⚠️ Rủi ro / cần bổ sung · ❌ Vi phạm (blocker)

---

## Bảng tổng quan

| # | Chính sách | Trạng thái | Finding |
|---|---|---|---|
| 1 | Payments | ❌ | P0-01 |
| 2 | Permissions and APIs that Access Sensitive Information | ❌ | P0-02 |
| 3 | Spam, Minimum Functionality, and Broken Functionality | ❌ | P0-03, P0-05, P0-06 |
| 4 | AI-Generated Content | ❌ | P0-04 |
| 5 | App account deletion requirements | ❌ | P0-07 |
| 6 | Misrepresentation / Deceptive Behavior | ❌ | P0-05, P0-06, P0-08 |
| 7 | User Data (Privacy policy, Data Safety) | ❌ | P0-08, P1-13 |
| 8 | Target API level | ⚠️ cần verify | P2-04 |
| 9 | 16 KB page size compatibility | ⚠️ cần verify | P2-04 |
| 10 | Families / Target audience & Content rating | ⚠️ | P2-13 |
| 11 | Device and Network Abuse | ⚠️ | P2-01 |
| 12 | Subscriptions (nếu gói PRO tự gia hạn) | ⚠️ | P0-01 |
| 13 | Data safety – Security practices (encryption in transit) | ✅ | — |
| 14 | Malware / Mobile Unwanted Software | ✅ | — |
| 15 | Impersonation | ✅ | — |
| 16 | Ads policy | ✅ (không có ads) | — |
| 17 | Device permissions — background location/SMS/Call Log | ✅ (không dùng) | — |
| 18 | Health apps / Financial services | ✅ (không thuộc phạm vi) | — |

---

## 1. ❌ Payments policy

**Link**: https://support.google.com/googleplay/android-developer/answer/9858738

### Điều khoản (nguyên văn)

> "Play-distributed apps requiring or accepting payment for access to in-app features or services… must use Google Play's billing system for those transactions unless Section 3, 8, or 9 applies."

> Cấm "leading users to other payment methods via: An app's listing in Google Play; In-app promotions… **In-app webviews, buttons, links, messaging, advertisements**… In-app user interface flows, including account creation or sign-up flows"

### Miễn trừ (và tại sao Nexora không thuộc)

| Miễn trừ | Nexora có thuộc? |
|---|---|
| Hàng hóa/dịch vụ vật lý (giao đồ ăn, vé xe, gym) | ❌ Không — gói PRO là quota AI, tiêu thụ trong app |
| Giao dịch P2P (đấu giá, quyên góp) | ❌ Không |
| Cờ bạc tiền thật | ❌ Không |
| Alternative billing program (một số quốc gia đủ điều kiện) | ❌ Chưa đăng ký |

### Trạng thái Nexora

❌ **Vi phạm.** `src/app/(app)/pricing/index.tsx:91` dùng `Linking.openURL(data.checkout.url)` để chuyển user sang cổng thanh toán ngoài mua gói PRO.

### Việc phải làm

- Chuyển sang Google Play Billing (`expo-iap` hoặc `react-native-purchases`).
- Tạo Subscription/In-app product trên Play Console tương ứng từng `planPriceId`.
- Backend verify purchase token qua **Google Play Developer API** trước khi cấp entitlement.
- **Xóa hoàn toàn** mọi code/UI dẫn user ra cổng ngoài trong bản Android. Không được để cả "link tham khảo".
- Giữ luồng cổng ngoài **chỉ cho web build** (nếu có), và không quảng bá nó trong app Android.

---

## 2. ❌ Permissions and APIs that Access Sensitive Information

**Nguyên tắc cốt lõi**: quyền phải **tối thiểu cần thiết** và **thực sự được sử dụng** cho tính năng đã công bố cho người dùng.

### Trạng thái Nexora

❌ **Vi phạm.**

| Quyền khai báo (`app.json:20-26`) | Có code native sử dụng? |
|---|---|
| `RECORD_AUDIO` | ❌ Không — không có `expo-audio`/`expo-av`, không gọi `requestRecordingPermissionsAsync()` ở đâu |
| `MODIFY_AUDIO_SETTINGS` | ❌ Không |
| `INTERNET` | ✅ (nhưng khai thủ công là dư — Expo tự thêm) |

Thêm vào đó, UI **báo sai trạng thái quyền**: `src/app/(app)/interview/preflight.tsx:711-716` giả lập "mic ready" bằng `setTimeout` trên Android.

### Việc phải làm — chọn 1 trong 2

**Đường A (giữ tính năng voice)**: cài `expo-audio`, gọi `requestRecordingPermissionsAsync()` thật, ghi âm thật, mic test đo mức âm thanh thật từ `useAudioRecorderState`.

**Đường B (cắt voice ở v1)**: xóa `RECORD_AUDIO` + `MODIFY_AUDIO_SETTINGS` khỏi `app.json`, xóa `NSMicrophoneUsageDescription`, ẩn toàn bộ UI voice, khóa `micMode = 'text'`, cập nhật chính sách + store listing cho khớp.

> Cả hai đường đều đóng được finding. Đường B nhanh hơn nhiều và **an toàn hơn về policy**.

---

## 3. ❌ Spam, Minimum Functionality, and Broken Functionality

**Điều khoản**: app không được crash, force close, hoặc có tính năng không hoạt động / không phản hồi.

### Trạng thái Nexora

❌ **Vi phạm ở 3 điểm:**

| Tính năng | Trạng thái trên Android | Vị trí |
|---|---|---|
| Phỏng vấn bằng giọng nói (STT) | Không nhận diện được gì — chỉ echo text cũ | `src/services/speech.ts:86-89` |
| AI interviewer đọc câu hỏi (TTS) | Hoàn toàn im lặng — dùng `new Audio()`, `SpeechSynthesisUtterance` (Web API) | `src/services/tts.ts:157-160, 192-195` |
| "Xuất dữ liệu cá nhân" | Không xuất gì, chỉ hiện Alert giả | `src/app/(app)/account/index.tsx:159-163` |
| Đổi / gỡ ảnh đại diện | Stub `Alert.alert` — nút thứ hai còn báo "Đã gỡ" dù không làm gì | `src/app/(app)/account/index.tsx:252, 260` |

### Việc phải làm

Xem [Phase 1](./03-REMEDIATION-PLAN.md). Nguyên tắc: **không để một nút chết nào trong bản submit** — hoặc làm cho nó hoạt động, hoặc ẩn nó đi.

---

## 4. ❌ AI-Generated Content policy

**Link**: https://support.google.com/googleplay/android-developer/answer/13985936

### Điều khoản (nguyên văn)

> "Apps that generate content using AI must contain **in-app user reporting or flagging features that allow users to report or flag offensive content to developers without needing to exit the app**."

> "Developers are expected to **utilize user reports to inform content filtering and moderation** in their apps."

### Phạm vi áp dụng

Policy nêu rõ bao gồm: *"Text-to-text AI chatbot apps, in which the AI generated chatbot interaction is a central feature"* — Nexora thuộc chính xác nhóm này.

### Trạng thái Nexora

❌ **Vi phạm.** Không có nút báo cáo nội dung ở bất kỳ surface AI nào:

| Surface có nội dung AI | Có nút báo cáo? |
|---|---|
| Câu hỏi phỏng vấn (`interview/[id].tsx`) | ❌ |
| Coaching realtime (`QuickCoachingModal.tsx`) | ❌ |
| Báo cáo & điểm số (`interview/report/[id].tsx`) | ❌ |
| Phân tích CV–JD (`cv-analysis/[id].tsx`) | ❌ |
| Learning path / skill profile (`growth/*`) | ❌ |
| STAR builder (`star-builder/index.tsx`) | ❌ |

`ProductFeedbackModal` là đánh giá sản phẩm 1–5 sao (`src/api/types/feedback.types.ts:3-7`), **không** phải kênh báo cáo nội dung.

### Việc phải làm

- Thêm affordance "Báo cáo nội dung không phù hợp" tại **mọi** surface trên.
- Form báo cáo có: loại vi phạm (chọn), mô tả tự do, và tham chiếu tự động tới `interviewId` / `questionId` / `analysisId`.
- Endpoint backend `POST /content-reports` + lưu trữ + quy trình xử lý nội bộ.
- Có xác nhận cho user rằng báo cáo đã được nhận.
- Ghi nhận trong chính sách rằng báo cáo được dùng để điều chỉnh moderation.

---

## 5. ❌ App account deletion requirements

**Link**: https://support.google.com/googleplay/android-developer/answer/13327111

### Điều khoản (nguyên văn)

> "provide users with an in-app path to delete their app accounts and associated data" — pathway phải "prominent (for example, within the account settings or a similar section)."

> "provide a **web link resource** where users can request app account deletion and associated data deletion." Link phải "functional", "relevant in scope", và "reference the app or developer name."

> "All developers will be prompted and required to answer a new set of questions in the **Data safety form** focused around deletion practices."

> "when deleting an account, you must also delete the user data associated with that app account."

> Nếu giữ lại dữ liệu vì lý do hợp pháp: "you must clearly inform users about your data retention practices, for example, within your privacy policy."

### Trạng thái Nexora

| Yêu cầu | Trạng thái |
|---|---|
| In-app path, dễ thấy | ✅ Có — Cài đặt tài khoản → Khu vực nguy hiểm (`account/index.tsx:628-645`) |
| Xóa tài khoản **và** dữ liệu liên quan | ⚠️ Chỉ là "deletion request" (`POST /me/deletion-requests`), chưa xác nhận được backend xóa thật |
| **Web deletion URL** | ❌ Chưa có / chưa xác minh |
| Khai báo trong Data safety form | ❌ Chưa làm |
| Công bố dữ liệu giữ lại | ❌ Chính sách nói "xóa hoàn toàn" nhưng thực tế phải giữ hóa đơn/log gian lận |

### Việc phải làm

1. Dựng trang `https://nexora.vn/xoa-tai-khoan` (hoặc `/account-deletion`): nêu tên app "Nexora AI", form nhập email, mô tả rõ dữ liệu nào bị xóa / dữ liệu nào giữ lại và trong bao lâu, thời gian xử lý.
2. Backend: `POST /me/deletion-requests` phải thực sự kích hoạt xóa (soft-delete ngay + hard-delete theo SLA), trả về `requestId` + `estimatedCompletionAt`.
3. App: hiển thị trạng thái yêu cầu xóa, cho phép huỷ trong thời gian grace period.
4. Cập nhật tab "Xóa dữ liệu" trong `legal-policy-modal.tsx` với danh sách dữ liệu giữ lại + lý do pháp lý.
5. Khai URL đó vào Play Console → Data safety → Data deletion.

---

## 6. ❌ Misrepresentation / Deceptive Behavior

**Điều khoản**: app không được mô tả sai chức năng, không được hiển thị thông tin sai cho người dùng, và metadata/chính sách phải khớp hành vi thực tế.

### Trạng thái Nexora

❌ **Vi phạm ở 6 điểm** — xem bảng chi tiết trong [`01-SECURITY-AUDIT.md` → P0-08](./01-SECURITY-AUDIT.md).

Ba điểm nghiêm trọng nhất:

1. `legal-policy-modal.tsx:438` nói dùng **"Google Play Billing"** — thực tế dùng cổng ngoài. Reviewer đọc chính sách trong app rồi thử flow thanh toán → thấy nói dối.
2. `account/index.tsx:159-163` báo **"Tệp dữ liệu cá nhân JSON đã được trích xuất thành công"** — không có file nào.
3. `account/index.tsx:260` báo **"Đã gỡ ảnh đại diện"** — không có gì bị gỡ.

### Việc phải làm

Nguyên tắc: **mọi câu khẳng định trong UI và chính sách phải kiểm chứng được bằng hành vi của app**. Sau khi fix P0-01…P0-07, rà lại toàn bộ văn bản.

---

## 7. ❌ User Data policy (Privacy policy + Data Safety)

### Yêu cầu

- Privacy policy URL **hoạt động**, khai báo trên Play Console **và** truy cập được trong app.
- Data Safety form khai đầy đủ: loại dữ liệu thu thập, mục đích, có chia sẻ với bên thứ ba không, có mã hóa khi truyền không, có cho phép xóa không.
- Mọi bên thứ ba nhận dữ liệu **phải được công bố**.
- Yêu cầu User Data áp dụng cả với tích hợp AI của bên thứ ba.

### Trạng thái Nexora

| Yêu cầu | Trạng thái |
|---|---|
| Có chính sách trong app | ✅ `legal-policy-modal.tsx` — khá đầy đủ về cấu trúc |
| Privacy policy URL hoạt động | ❌ `https://nexora.vn/privacy` — **chưa xác minh** |
| Công bố việc gửi giọng nói tới **Microsoft Azure Speech** | ❌ Không có. Code gửi tới `https://{region}.tts.speech.microsoft.com` (`src/services/tts.ts:121`) |
| Công bố việc gửi CV/JD tới nhà cung cấp LLM (OpenAI/Azure OpenAI/Gemini?) | ❌ Không có — cần xác nhận với backend team ai là LLM provider |
| Mã hóa khi truyền | ✅ HTTPS-only, không có cleartext |
| Cơ chế consent cho analytics | ❌ Chưa có (`src/services/analytics.ts`) |
| Data Safety form | ❌ Chưa làm |

### Việc phải làm

Xem [`04-PLAY-CONSOLE-CHECKLIST.md`](./04-PLAY-CONSOLE-CHECKLIST.md) — có bảng khai Data Safety soạn sẵn theo từng loại dữ liệu Nexora thực sự thu thập.

---

## 8. ⚠️ Target API level requirements

**Link**: https://support.google.com/googleplay/android-developer/answer/11926878

### Yêu cầu hiện hành

> Từ **31-08-2026**, app mới và bản cập nhật phải target **Android 16 (API 36)** hoặc cao hơn để được submit lên Google Play. App hiện có phải target Android 15 (API 35) hoặc cao hơn để tiếp tục khả dụng với user mới trên thiết bị có OS cao hơn target của app.
>
> Có thể xin gia hạn tới **01-11-2026** nếu cần thêm thời gian.

### Trạng thái Nexora

⚠️ **Chưa verify.** `app.json` không khai `targetSdkVersion` tường minh — phụ thuộc hoàn toàn default của Expo SDK 57 (dự kiến là API 36, nhưng **phải kiểm tra AAB thật**).

### Việc phải làm

1. Thêm `expo-build-properties` và khóa tường minh:
   `compileSdkVersion: 36`, `targetSdkVersion: 36`, `minSdkVersion: 24` (hoặc theo default SDK 57).
2. Sau khi build, verify bằng `aapt2 dump badging <aab>` hoặc kiểm `android/app/build.gradle` sau `expo prebuild`.
3. Ghi bằng chứng vào `docs/release-audit/evidence/`.

---

## 9. ⚠️ 16 KB page size compatibility

**Link**: https://developer.android.com/guide/practices/page-sizes

### Yêu cầu

> Từ **01-11-2025**, mọi app mới và bản cập nhật submit lên Google Play targeting Android 15+ phải hỗ trợ 16 KB page size. Play Console sẽ **chặn** release nếu không đạt.
>
> Chỉ ảnh hưởng app có native code. NDK r28+ compile 16 KB-aligned mặc định.

### Trạng thái Nexora

⚠️ **Chưa verify.** App có nhiều native lib:
`react-native-reanimated@4.5.1`, `react-native-worklets@0.10.1`, `react-native-screens`, `react-native-svg`, `react-native-gesture-handler`, `expo-secure-store`, `lottie-react-native`, Hermes.

Expo SDK 57 / RN 0.86 dùng NDK mới nên **lý thuyết là đạt**, nhưng `lottie-react-native@7.5.0` và `react-native-keyboard-aware-scroll-view@0.9.5` (lib cũ, ít bảo trì) cần kiểm riêng.

### Việc phải làm

1. Build AAB production, giải nén, chạy script `check_elf_alignment.sh` của Android trên mọi `.so`.
2. Test trên **emulator Android 15 với 16 KB system image**.
3. Nếu có lib fail → xóa (`lottie-react-native` đang **không được dùng**, xóa luôn) hoặc thay thế.

---

## 10. ⚠️ Target audience, Content rating & Families policy

### Trạng thái Nexora

⚠️ **Rủi ro mâu thuẫn.** `legal-policy-modal.tsx:359` nói:

> "dành cho người dùng từ 18 tuổi trở lên (**hoặc từ 13 tuổi với sự giám sát của người giám hộ**). Chúng tôi không chủ động thu thập thông tin cá nhân của trẻ em dưới 13 tuổi."

Phát biểu vừa nói 18+ vừa cho 13+ → mơ hồ.

### Rủi ro

Nếu Play Console khai target audience **có bao gồm trẻ em/teen** → kích hoạt **Families policy** với yêu cầu khắt khe hơn nhiều (ads, thu thập dữ liệu, SDK phải trong danh sách approved). Với app xử lý CV và thanh toán, điều này rất phiền.

### Việc phải làm

- **Quyết định dứt khoát**: đặt target audience **18+** trên Play Console.
- Sửa `legal-policy-modal.tsx:359` thành **chỉ 18+**, bỏ mệnh đề 13+.
- Trả lời Content Rating questionnaire khớp: có UGC (câu trả lời phỏng vấn), có mua hàng trong app, không có bạo lực/tình dục/cờ bạc.
- Khai app **có** chứa nội dung do AI tạo.

---

## 11. ⚠️ Device and Network Abuse

**Điều khoản**: app không được gây tiêu thụ pin/data/tài nguyên bất thường.

### Trạng thái Nexora

⚠️ **Rủi ro nhẹ.** Do `signalr.ts` chưa có kết nối realtime thật (`@microsoft/signalr` chưa được cài), app bù bằng polling:

- `refetchInterval` liên tục khi interview ở trạng thái `starting`/`completing`/`evaluating` (`useInterviewSession.ts:48-60`)
- Poll báo cáo mỗi **3 giây** (`interview/report/[id].tsx:95`)

Với phiên phỏng vấn dài + AI xử lý chậm, đây là lượng request đáng kể. Chưa tới mức vi phạm nhưng ảnh hưởng Play Vitals (excessive network/wakeups) và trải nghiệm.

### Việc phải làm

Hoàn thiện SignalR thật hoặc chuyển sang exponential backoff (3s → 5s → 10s → 30s) + dừng poll khi app vào background.

---

## 12. ⚠️ Subscriptions policy

Áp dụng **nếu** gói PRO là subscription tự gia hạn. `PlanPriceResponseV2` có `durationDays` (`billing.types.ts:13`) → nghe giống gói theo kỳ hạn.

### Yêu cầu nếu là subscription

- Công bố rõ **giá, chu kỳ gia hạn, ngày tính phí tiếp theo** trước khi user xác nhận.
- Cung cấp đường dẫn quản lý/hủy subscription.
- Không được gây nhầm lẫn giữa free trial và bản trả phí.
- Hoàn tiền tuân theo chính sách Google Play, **không phải** chính sách riêng.

### Trạng thái Nexora

⚠️ `legal-policy-modal.tsx:456-463` hiện đang công bố **chính sách hoàn tiền riêng** ("trong vòng 7 ngày làm việc… qua email support@nexora.vn"). Khi chuyển sang Google Play Billing, chính sách này **phải được viết lại** cho khớp với quy trình hoàn tiền của Google, nếu không sẽ mâu thuẫn.

---

## 13–18. ✅ Các chính sách đã đạt

| Chính sách | Lý do đạt |
|---|---|
| **Data safety – Security practices** | Toàn bộ traffic HTTPS; không có cleartext (grep `http://` chỉ trả về XML namespace) |
| **Malware / Mobile Unwanted Software** | Không có `eval`, `new Function`, không tải/thực thi code động, không có dynamic native loading, không obfuscate ác ý |
| **Impersonation** | Branding "Nexora AI" là của chính developer; không mạo danh tổ chức khác |
| **Ads policy** | Không có SDK quảng cáo nào trong dependencies |
| **Device permissions nhạy cảm** (background location, SMS, Call Log, QUERY_ALL_PACKAGES, All files access) | Không dùng quyền nào trong nhóm này |
| **Health apps / Financial services** | Không thuộc phạm vi — app là công cụ luyện phỏng vấn |
| **Elections / Gambling / Illegal content** | Không liên quan |

---

## Ma trận điều kiện submit

Một dòng ❌ nào còn lại = **không submit**.

| Điều kiện bắt buộc | Trạng thái | Blocker |
|---|---|---|
| Không có luồng thanh toán ngoài Google Play trong app Android | ❌ | P0-01 |
| Mọi quyền khai báo đều được sử dụng thật và xin lúc runtime | ❌ | P0-02 |
| Không có tính năng chính bị hỏng trên thiết bị Android thật | ❌ | P0-03 |
| Có cơ chế báo cáo nội dung AI trong app | ❌ | P0-04 |
| Không có nút/tính năng giả trong UI | ❌ | P0-05, P0-06 |
| Có in-app account deletion **và** web deletion URL hoạt động | ❌ | P0-07 |
| Privacy policy URL hoạt động và nội dung khớp hành vi app | ❌ | P0-08 |
| Data Safety form hoàn thành và chính xác | ❌ | — |
| Content rating questionnaire hoàn thành | ❌ | — |
| Target API 36 verified trên AAB | ⚠️ | P2-04 |
| 16 KB page size verified trên AAB | ⚠️ | P2-04 |
| Không có quyền lạ xuất hiện trong manifest sau prebuild | ⚠️ | P2-08 |
| Crash-free rate đo được (có crash reporting) | ⚠️ | P1-11 |
| R8/minify bật cho release build | ⚠️ | P1-02 |
