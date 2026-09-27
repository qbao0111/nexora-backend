# 01 — Báo cáo kiểm thử bảo mật & rà soát mã nguồn

**Phương pháp**: SAST thủ công toàn bộ `src/` (160 file), rà soát cấu hình build (`app.json`, `eas.json`, `metro.config.js`, `tsconfig.json`), phân tích thành phần phụ thuộc (`npm audit`), rà soát luồng dữ liệu (auth, upload, thanh toán, speech), đối chiếu chính sách Google Play.

**Không thực hiện**: DAST/pentest runtime, phân tích APK đã build, test backend API (ngoài phạm vi repo này).

**Ký hiệu**: `file:line` là vị trí chính xác của bằng chứng tại commit `c59e138`.

---

# PHẦN A — P0: BLOCKER

---

## P0-01 · Bán nội dung số qua cổng thanh toán ngoài Google Play

| | |
|---|---|
| **Mức** | 🔴 P0 — Blocker (nguy cơ đình chỉ tài khoản) |
| **Loại** | Vi phạm chính sách thanh toán |
| **Vị trí** | `src/app/(app)/pricing/index.tsx:73-104`, `src/api/pricing.api.ts:11-19`, `src/api/types/billing.types.ts:33-46` |

### Mô tả

App bán gói PRO — mở khóa quota phỏng vấn AI, phân tích CV, giới hạn câu hỏi — tức là **nội dung/tính năng số tiêu thụ trong app**. Luồng hiện tại:

1. User bấm chọn gói → `checkoutMutation.mutate(price.id)` (`pricing/index.tsx:331`)
2. Client gọi `POST /checkout-sessions` → backend trả `CheckoutResponse` có `checkout.url` và `provider`
3. Client **mở URL cổng thanh toán ngoài**:

```
// src/app/(app)/pricing/index.tsx:88-93
onPress: () => {
  if (data.checkout?.url) {
    Linking.openURL(data.checkout.url);
  }
},
```

`CheckoutActionResponse` (`billing.types.ts:33-37`) có cả `method`, `url`, `fields` — đây là hình dạng điển hình của form redirect tới VNPay / MoMo / PayOS, không phải Google Play Billing.

### Chính sách bị vi phạm

**Google Play Payments policy** — nguyên văn:

> "Play-distributed apps requiring or accepting payment for access to in-app features or services… must use Google Play's billing system for those transactions unless Section 3, 8, or 9 applies."

Và cấm rõ việc dẫn user ra ngoài:

> "leading users to other payment methods via: An app's listing in Google Play; In-app promotions… **In-app webviews, buttons, links, messaging, advertisements**… In-app user interface flows, including account creation or sign-up flows"

Nexora **không thuộc miễn trừ nào**: không phải hàng hóa vật lý, không phải P2P, không phải cờ bạc, và chưa đăng ký alternative billing program.

### Tác động

- Reject 100% khi reviewer thử flow thanh toán.
- Nguy cơ **terminate developer account** — Google coi vi phạm thanh toán là cố ý lách phí.
- Nếu đã có user thật thanh toán qua cổng ngoài, còn phát sinh vấn đề hoàn tiền/tranh chấp.

### Hướng xử lý

Chuyển sang Google Play Billing bằng `expo-iap` hoặc `react-native-purchases` (RevenueCat) — hai thư viện Expo chính thức khuyến nghị cho SDK 57. Xem [`03-REMEDIATION-PLAN.md` → Phase 2](./03-REMEDIATION-PLAN.md).

> ⚠️ **Quyết định business bắt buộc trước khi code**: Google thu 15–30% doanh thu. Giá gói PRO hiện tại (`amountMinor`) phải được tính lại.

---

## P0-02 · Quyền `RECORD_AUDIO` khai báo nhưng không dùng; bài test microphone giả lập thành công

| | |
|---|---|
| **Mức** | 🔴 P0 — Blocker (nguy cơ đình chỉ tài khoản) |
| **Loại** | Vi phạm chính sách quyền + lừa dối người dùng |
| **Vị trí** | `app.json:20-26`, `src/app/(app)/interview/preflight.tsx:703-721`, `src/services/speech.ts:41-53` |

### Mô tả

#### 2a. Quyền được khai báo nhưng không có code native nào sử dụng

```
// app.json:20-26
"permissions": [
  "RECORD_AUDIO",
  "INTERNET",
  "MODIFY_AUDIO_SETTINGS"
],
```

Trong toàn bộ `src/`:
- **Không có** `expo-audio` / `expo-av` trong `package.json` — không có API ghi âm native nào.
- **Không có** lệnh gọi `requestRecordingPermissionsAsync()` hay `PermissionsAndroid.request()` ở đâu cả.
- `speechService.checkPermission()` (`speech.ts:41-53`) chỉ chạy nhánh `Platform.OS === 'web'`; trên Android nó **luôn trả `'prompt'`** và không bao giờ xin quyền.

Kết quả: app xin quyền microphone trong manifest, hiện lên trong mục "Permissions" của Play Store listing, nhưng **không có chức năng nào dùng đến nó trên Android**.

#### 2b. Bài kiểm tra microphone giả lập thành công

```
// src/app/(app)/interview/preflight.tsx:703-721
const handleTest = async () => {
  setMicStatus('testing');
  try {
    if (typeof navigator !== 'undefined' && navigator.mediaDevices?.getUserMedia) {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      stream.getTracks().forEach((track) => track.stop());
      setMicStatus('ready');
      onModeChange?.('voice');
    } else {
      setTimeout(() => {
        setMicStatus('ready');      // ← GIẢ LẬP THÀNH CÔNG
        onModeChange?.('voice');
      }, 1200);
    }
  } catch {
```

Trên Android, `navigator.mediaDevices` **không tồn tại** → luôn rơi vào nhánh `else` → sau 1,2 giây UI báo **"Microphone đã sẵn sàng"** dù chưa hề xin quyền, chưa hề đo tín hiệu.

Tệ hơn, thanh đo mức âm thanh cũng là số ngẫu nhiên:

```
// src/app/(app)/interview/preflight.tsx:691
setMeterLevel(Math.floor(Math.random() * 60) + 30);
```

### Chính sách bị vi phạm

- **Permissions and APIs that Access Sensitive Information**: quyền phải là "minimum necessary" và **phải được dùng thực sự** cho tính năng đã công bố. Khai báo quyền không dùng là vi phạm.
- **Misrepresentation / Deceptive Behavior**: UI báo trạng thái thiết bị không đúng sự thật.
- **Android runtime permission model**: `RECORD_AUDIO` là dangerous permission — bắt buộc xin lúc runtime, không chỉ khai báo manifest.

### Tác động

- Reviewer thử "Kiểm tra microphone" → thấy báo OK → vào phòng phỏng vấn → mic không hoạt động → reject với lý do broken functionality **và** unnecessary permission.
- Người dùng bị lừa tin thiết bị đã sẵn sàng, mất bài phỏng vấn.

### Hướng xử lý

Cài `expo-audio` (có trong SDK 57), gọi `requestRecordingPermissionsAsync()` thực sự, đo mức âm thanh thật qua `useAudioRecorderState`. Nếu không kịp làm voice trên native → **xóa `RECORD_AUDIO` và `MODIFY_AUDIO_SETTINGS` khỏi `app.json`**, khóa chế độ text-only.

---

## P0-03 · Tính năng phỏng vấn bằng giọng nói không hoạt động trên Android

| | |
|---|---|
| **Mức** | 🔴 P0 — Blocker |
| **Loại** | Broken functionality — tính năng cốt lõi |
| **Vị trí** | `src/services/speech.ts:22-90`, `src/services/tts.ts:35-227`, `src/components/interview/useInterviewSession.ts:28-42,155-178` |

### Mô tả

Toàn bộ lớp âm thanh được viết bằng **Web API thuần**, không có implementation native.

#### 3a. Speech-to-text (`src/services/speech.ts`)

```
// src/services/speech.ts:22-32
private initRecognition() {
  if (Platform.OS === 'web' && typeof window !== 'undefined') {
    const SpeechRecognition = (window as any).SpeechRecognition || (window as any).webkitSpeechRecognition;
    ...
  }
}
```

Trên Android `this.recognition` luôn là `null`. Nhưng:

```
// src/services/speech.ts:34-39
public isAvailable(): boolean {
  if (Platform.OS === 'web') {
    return !!this.recognition;
  }
  return true;              // ← BÁO KHẢ DỤNG TRÊN NATIVE DÙ KHÔNG CÓ GÌ
}
```

Và khi bắt đầu ghi:

```
// src/services/speech.ts:86-89
} else {
  this.isListening = true;
  listener.onResult(initialBaseText, false);   // ← echo lại text cũ, không nhận diện gì
}
```

→ Trên Android, nhấn "Nói" sẽ hiện trạng thái đang ghi âm, nhưng **không có chữ nào được nhận diện, vĩnh viễn**.

#### 3b. Text-to-speech (`src/services/tts.ts`)

Hàm `speakWithAzureRest()` gọi Azure TTS REST rồi phát bằng API **chỉ có trên browser**:

```
// src/services/tts.ts:157-160
const blob = await response.blob();
const audioUrl = URL.createObjectURL(blob);   // ← không tồn tại trong RN (Hermes)
const audio = new Audio(audioUrl);            // ← không tồn tại trong RN
```

Khi throw, catch block fallback sang `speakWithLocalVoice()` — cũng là Web API:

```
// src/services/tts.ts:192-195
if (typeof window !== 'undefined' && 'speechSynthesis' in window) {
  window.speechSynthesis.cancel();
  const utterance = new SpeechSynthesisUtterance(text);
```

Trên Android nhánh này false → rơi vào `else` (dòng 219-222) → `onDone?.()` ngay lập tức. **AI interviewer hoàn toàn im lặng.**

Lưu ý thêm: `src/config/speech.ts:2` cấu hình giọng đọc là `de-DE-Seraphina:DragonHDLatestNeural` — **giọng tiếng Đức** cho app tiếng Việt, trong khi SSML khai `xml:lang='vi-VN'` (`tts.ts:27`). Kể cả khi chạy được trên web, giọng đọc cũng sai ngôn ngữ.

### Chính sách bị vi phạm

- **Spam, Minimum Functionality, and Broken Functionality**: app không được có tính năng chính không hoạt động, crash, hoặc không phản hồi.
- Liên đới **Misrepresentation**: store listing sẽ quảng cáo "phỏng vấn bằng giọng nói".

### Tác động

App về cơ bản chỉ còn là form nhập text. Toàn bộ giá trị khác biệt của sản phẩm mất trên nền tảng mục tiêu.

### Hướng xử lý

Xem [Phase 1, Bước 1.2 – 1.4](./03-REMEDIATION-PLAN.md).

---

## P0-04 · Không có cơ chế báo cáo nội dung AI trong app

| | |
|---|---|
| **Mức** | 🔴 P0 — Blocker |
| **Loại** | Vi phạm AI-Generated Content policy |
| **Vị trí** | Thiếu ở `src/app/(app)/interview/report/[id].tsx`, `src/app/(app)/interview/[id].tsx`, `src/components/interview/QuickCoachingModal.tsx`, `src/app/(app)/cv-analysis/[id].tsx` |

### Mô tả

Nexora sinh nội dung AI ở rất nhiều nơi: câu hỏi phỏng vấn, nhận xét coaching realtime (`QuickCoachingModal`), báo cáo điểm số + đánh giá năng lực, phân tích CV–JD, gợi ý learning path, STAR builder.

Rà soát toàn bộ các màn này: **không có nút "Báo cáo nội dung không phù hợp"** ở bất kỳ điểm nào có nội dung AI.

Tính năng gần nhất là `src/components/feedback/ProductFeedbackModal.tsx` + `src/api/feedback.api.ts`, nhưng đó là **đánh giá sản phẩm 1–5 sao** (`FeedbackRequest { rating, comment, allowPublicDisplay }`), có `allowPublicDisplay` để đăng làm testimonial công khai — đây là review, **không phải kênh báo cáo nội dung**.

Icon `flag-outline` tại `src/app/(app)/interview/report/[id].tsx:305` chỉ là icon trang trí cho section, không gắn với hành động báo cáo.

### Chính sách bị vi phạm

**Google Play AI-Generated Content policy** — nguyên văn:

> "Apps that generate content using AI must contain **in-app user reporting or flagging features that allow users to report or flag offensive content to developers without needing to exit the app**."

> "Developers are expected to utilize user reports to inform content filtering and moderation in their apps."

App thuộc phạm vi policy: là app "text-to-text AI" nơi tương tác với AI là tính năng trung tâm.

### Tác động

Đây là điểm reviewer kiểm rất chặt với app AI từ 2024 trở đi. Reject chắc chắn nếu không có.

### Hướng xử lý

Thêm nút báo cáo tại **mọi** surface có nội dung AI + endpoint backend `POST /content-reports` + cam kết dùng report để điều chỉnh moderation. Chi tiết: [Phase 3, Bước 3.1](./03-REMEDIATION-PLAN.md).

---

## P0-05 · Chức năng "Xuất dữ liệu cá nhân" không xuất gì

| | |
|---|---|
| **Mức** | 🔴 P0 — Blocker |
| **Loại** | Broken functionality + Misrepresentation + vi phạm cam kết data portability |
| **Vị trí** | `src/app/(app)/account/index.tsx:155-169` |

### Mô tả

```
// src/app/(app)/account/index.tsx:156-169
const handleExportData = async () => {
  try {
    setIsExporting(true);
    const data = await userApi.exportData();        // ← biến `data` KHÔNG BAO GIỜ ĐƯỢC DÙNG
    Alert.alert(
      'Xuất Dữ Liệu Thành Công',
      `Tệp dữ liệu cá nhân JSON đã được trích xuất thành công cho tài khoản ${currentUserData?.email}.`
    );
  } catch (err: any) {
```

Kết quả `userApi.exportData()` (`src/api/user.api.ts:33-36`) được tải về rồi **bỏ đi**. Không ghi file, không share, không download. Alert nói "đã trích xuất thành công" là **hoàn toàn sai sự thật**.

Đồng thời chính sách trong app cam kết:

```
// src/components/ui/legal-policy-modal.tsx:344
"Bạn có toàn quyền trích xuất bản sao dữ liệu cá nhân (định dạng JSON)..."
```

### Tác động phụ về bảo mật

Toàn bộ PII của user (CV, lịch sử phỏng vấn, báo cáo) được **tải xuống bộ nhớ tiến trình rồi để garbage collector xử lý** — không chủ đích, tăng bề mặt rủi ro nếu có memory dump / crash log.

### Chính sách bị vi phạm

- **Broken Functionality**
- **Misrepresentation**: thông báo cho user điều không xảy ra
- **User Data policy**: cam kết trong privacy policy phải khớp hành vi thực tế của app

### Hướng xử lý

Ghi ra file bằng `expo-file-system`, cho user share/save bằng `expo-sharing`. Chi tiết: [Phase 1, Bước 1.5](./03-REMEDIATION-PLAN.md).

---

## P0-06 · Nút quản lý ảnh đại diện là stub không hoạt động

| | |
|---|---|
| **Mức** | 🔴 P0 — Blocker |
| **Loại** | Broken functionality |
| **Vị trí** | `src/app/(app)/account/index.tsx:252, 260` |

### Mô tả

```
// src/app/(app)/account/index.tsx:252
onPress={() => Alert.alert('Thông báo', 'Chọn ảnh đại diện mới từ thư viện ảnh.')}
// src/app/(app)/account/index.tsx:260
onPress={() => Alert.alert('Thông báo', 'Đã gỡ ảnh đại diện.')}
```

Nút thứ hai còn tệ hơn nút thứ nhất: nó **báo đã gỡ ảnh đại diện thành công** trong khi không làm gì. User tin rằng ảnh đã bị xóa.

Đây là hai stub duy nhất còn lại trong codebase (grep `Alert.alert('Thông báo'` → chỉ 2 kết quả), nên chi phí sửa rất thấp so với mức độ rủi ro.

### Chính sách bị vi phạm

**Spam, Minimum Functionality, and Broken Functionality** — reviewer bấm thử là thấy ngay.

### Hướng xử lý

Hai lựa chọn — **cả hai đều đóng được finding**:
- **A (khuyến nghị nếu backend đã có endpoint)**: tích hợp `expo-image-picker` + upload thật.
- **B (nhanh, an toàn)**: **ẩn hoàn toàn 2 nút này** khỏi UI cho v1. Không để nút chết trong bản submit.

---

## P0-07 · Xóa tài khoản chưa đáp ứng yêu cầu Play; thiếu URL xóa trên web

| | |
|---|---|
| **Mức** | 🔴 P0 — Blocker |
| **Loại** | Vi phạm App account deletion requirements |
| **Vị trí** | `src/api/user.api.ts:38-40`, `src/app/(app)/account/index.tsx:171-198`, `src/components/ui/legal-policy-modal.tsx:471-500` |

### Mô tả

App **đã có** đường dẫn xóa trong app (tốt): Cài đặt tài khoản → Khu vực nguy hiểm → Yêu cầu xóa. Nhưng còn 3 khoảng trống:

1. **Chỉ là "request", không phải delete**: `requestDeletion()` gọi `POST /me/deletion-requests`. Không có xác nhận nào rằng dữ liệu sẽ bị xóa; Alert nói "đã tiếp nhận" rồi logout. Không có UI cho user theo dõi/huỷ yêu cầu.
2. **Thiếu web deletion URL**: Play Console **bắt buộc** một link web công khai để user (kể cả đã xoá app) yêu cầu xóa tài khoản. Chưa thấy link này ở đâu trong repo và chưa xác minh `nexora.vn` có trang này.
3. **Thiếu công bố dữ liệu giữ lại**: chính sách nói xóa "hoàn toàn trong tối đa 30 ngày" (`legal-policy-modal.tsx:497`) nhưng không nêu dữ liệu nào được giữ lại vì lý do hợp pháp (hóa đơn, chống gian lận) — trong khi thực tế hệ thống thanh toán chắc chắn phải giữ.

### Chính sách bị vi phạm

**App account deletion requirements** — nguyên văn:

> "provide users with an in-app path to delete their app accounts and associated data" — pathway phải "prominent (for example, within the account settings or a similar section)."

> "provide a **web link resource** where users can request app account deletion and associated data deletion." Link phải "functional", "relevant in scope", và "reference the app or developer name."

> "All developers will be prompted and required to answer a new set of questions in the Data safety form focused around deletion practices."

> Nếu có giữ lại dữ liệu: "you must clearly inform users about your data retention practices, for example, within your privacy policy."

### Hướng xử lý

Xem [Phase 3, Bước 3.2](./03-REMEDIATION-PLAN.md).

---

## P0-08 · Nội dung pháp lý trong app mô tả sai thực tế sản phẩm

| | |
|---|---|
| **Mức** | 🔴 P0 — Blocker |
| **Loại** | Misrepresentation / Deceptive Behavior |
| **Vị trí** | `src/components/ui/legal-policy-modal.tsx:253, 336, 344, 352, 438, 486, 507` |

### Mô tả — 6 sai lệch cụ thể

| # | Dòng | Nội dung công bố | Thực tế trong code |
|---|---|---|---|
| 1 | `:438` | "…hoặc cơ chế thanh toán trong ứng dụng (**Google Play Billing**)" | Dùng `Linking.openURL()` ra cổng ngoài — xem P0-01 |
| 2 | `:344` | "trích xuất bản sao dữ liệu cá nhân (định dạng **JSON**)" | Không xuất gì — xem P0-05 |
| 3 | `:352` | "Ứng dụng yêu cầu quyền truy cập Micro **duy nhất cho mục đích thu âm** câu trả lời" | Quyền được khai báo nhưng không dùng trên Android — xem P0-02 |
| 4 | `:336` | "Dữ liệu ghi âm giọng nói khi thực hiện phỏng vấn (chỉ dùng cho chuyển đổi văn bản và phân tích giọng nói)" | Không có ghi âm nào trên native; **và** nếu có, chưa công bố rằng audio đi qua **Microsoft Azure Speech** (bên thứ ba) |
| 5 | `:507` | "Cập nhật lần cuối: **25.09.2026**" | Ngày này gần như chắc chắn là lỗi nhập (định dạng và tính hợp lệ cần kiểm lại) — reviewer coi ngày sai là dấu hiệu tài liệu copy-paste |
| 6 | `:253`, `:507` | Link "Đọc bản đầy đủ trên nexora.vn" → `https://nexora.vn/privacy` | **Chưa xác minh URL này sống**. Play Console bắt buộc privacy policy URL hoạt động |

### Vấn đề bảo mật thực chất (không chỉ là văn bản)

Sai lệch #4 là vấn đề **User Data policy** thật sự: giọng nói của user được gửi tới `https://{region}.tts.speech.microsoft.com` (`src/services/tts.ts:121`) và speech token lấy từ backend Nexora — tức **có chia sẻ dữ liệu với Microsoft Azure**. Chính sách hiện tại chỉ nói "không bán cho bên thứ ba vì mục đích quảng cáo" (`:340`), **không công bố việc chuyển dữ liệu sang Azure**. Data Safety form cũng phải khai điều này.

### Hướng xử lý

Viết lại toàn bộ nội dung pháp lý cho khớp hành vi thật sau khi fix P0-01…P0-07, dựng trang web privacy + terms + data deletion. Chi tiết: [Phase 3, Bước 3.3](./03-REMEDIATION-PLAN.md).

---

# PHẦN B — P1: HIGH (bảo mật)

---

## P1-01 · Logger ghi chi tiết lỗi ra console ở bản production

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `src/services/logger.ts:27-35`, `src/api/client.ts:98-102` |

```
// src/services/logger.ts:27-35
public error(message: string, error?: unknown, context?: LogContext) {
  const formatted = this.formatLog('ERROR', message, context);
  console.error(formatted, error || '');       // ← KHÔNG có guard __DEV__

  if (!__DEV__) {
    // In production builds, this feeds telemetry loggers like Sentry.captureException(error)
  }                                            // ← comment rỗng, chưa tích hợp gì
}
```

`info()` và `warn()` có guard `__DEV__` đúng, nhưng `error()` thì không. Interceptor lỗi của axios gọi nó với đầy đủ ngữ cảnh:

```
// src/api/client.ts:98-102
logger.error(`API Error [${method}] ${error.config?.url}`, error, {
  requestId, code: errorCode, status: error.response?.status,
});
```

Object `AxiosError` được truyền nguyên → `console.error` sẽ in ra **request config (bao gồm header `Authorization: Bearer <token>`)**, URL đầy đủ, và response body.

Ngoài ra còn 6 chỗ `console.error`/`console.warn` trực tiếp không guard:
- `src/services/storage.ts:26, 47, 67` — in lỗi SecureStore kèm tên key
- `src/app/(app)/resumes/index.tsx:73` — `console.error(error)` với lỗi upload CV
- `src/app/(app)/career-goals/create.tsx` — 1 chỗ
- `src/services/tts.ts:136, 170, 179` — in lỗi Azure TTS
- `src/services/speech.ts:70, 136, 186`

### Tác động

Trên Android, `console.error` đi vào **logcat**. Bất kỳ app nào có `READ_LOGS` (hoặc ADB, hoặc crash reporter của bên thứ ba trên máy) đều đọc được → **rò rỉ access token và PII**. Đây là lỗ hổng OWASP Mobile M9 (Insecure Logging).

### Sửa

Bọc `console.error` trong `if (__DEV__)`; ở production gửi tới crash reporter đã **scrub** header `Authorization` và body. Chi tiết: [Phase 4, Bước 4.1](./03-REMEDIATION-PLAN.md).

---

## P1-02 · Không bật R8/minify, không có `expo-build-properties`

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `app.json` (thiếu plugin), `package.json` (thiếu dependency) |

`package.json` **không có** `expo-build-properties`. Hệ quả:

- `enableMinifyInReleaseBuilds` không bật → **R8 không obfuscate** code Java/Kotlin, tên class/method native lộ nguyên.
- `enableShrinkResourcesInReleaseBuilds` không bật → APK/AAB to hơn cần thiết.
- `usesCleartextTraffic` không set `false` tường minh → không có bảo đảm ở tầng manifest rằng app từ chối HTTP.
- `targetSdkVersion` / `compileSdkVersion` không khóa tường minh → phụ thuộc hoàn toàn default của SDK 57, khó audit khi Google đổi deadline.

> ⚠️ Lưu ý tên option đã đổi: trong **SDK 57** là `enableMinifyInReleaseBuilds`, **không phải** `enableProguardInReleaseBuilds` như các SDK cũ. Xem https://docs.expo.dev/versions/v57.0.0/sdk/build-properties/

### Sửa

[Phase 5, Bước 5.1](./03-REMEDIATION-PLAN.md).

---

## P1-03 · Refresh token được lưu vào SecureStore nhưng không bao giờ được dùng

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `src/context/auth-context.tsx:97-99`, `src/services/storage.ts:75-76`, `src/api/client.ts:152-162` |

### Mô tả

Khi login, refresh token được ghi vào SecureStore:

```
// src/context/auth-context.tsx:97-99
if (res.refreshToken) {
  await tokenStorage.setRefreshToken(res.refreshToken);
}
```

Nhưng lúc refresh, request **không hề gửi token đó** — nó dựa vào cookie `withCredentials`:

```
// src/api/client.ts:152-162
const refreshResponse = await axios.post(
  `${API_BASE_URL}/auth/refresh`,
  {},                                  // ← body rỗng, không có refreshToken
  { withCredentials: true, headers: { 'Content-Type': 'application/json' } }
);
```

`tokenStorage.getRefreshToken()` (`storage.ts:75`) **không được gọi ở bất kỳ đâu** trong codebase.

### Hai vấn đề

1. **Bảo mật**: một credential dài hạn (refresh token) được persist trên thiết bị mà không có mục đích sử dụng → tăng bề mặt tấn công vô ích. Nguyên tắc tối thiểu hóa dữ liệu bị vi phạm.
2. **Chức năng**: luồng refresh phụ thuộc cookie `nexora_refresh_token`. Trên React Native Android, cookie do OkHttp `CookieManager` quản lý và **không được đảm bảo persist qua lần khởi động app**. Hệ quả rất dễ gặp: user mở lại app sau vài giờ → `hydrateSessionAsync` (`auth-context.tsx:28-51`) thấy không có access token → gọi refresh → cookie đã mất → `clearTokens()` → **bị đăng xuất bất ngờ**, dù refresh token vẫn nằm trong SecureStore.

### Sửa

Chọn **một** chiến lược và làm nhất quán — khuyến nghị: gửi refresh token trong body cho native, dùng cookie chỉ cho web. Chi tiết: [Phase 4, Bước 4.3](./03-REMEDIATION-PLAN.md).

---

## P1-04 · Route group `(app)` không có auth guard

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `src/app/(app)/_layout.tsx:1-18` |

```
// src/app/(app)/_layout.tsx — TOÀN BỘ FILE
import { Stack } from 'expo-router';

export default function AppLayout() {
  return (
    <Stack screenOptions={{ headerShown: false }}>
      <Stack.Screen name="account" options={{ headerShown: false }} />
      ... 10 screen, KHÔNG có kiểm tra isAuthenticated
    </Stack>
  );
}
```

Đối chiếu: `src/app/(tabs)/_layout.tsx` **có** guard (dùng `useAuth()` + `Redirect`), còn `(app)` thì không.

### PoC

App khai `"scheme": "nexoramobile"` (`app.json:7`). Khi chưa đăng nhập:

```
adb shell am start -W -a android.intent.action.VIEW \
  -d "nexoramobile://(app)/account" com.nexora.app
```

→ Màn hình Cài đặt tài khoản render, các query TanStack chạy, gọi `/me`, `/me/billing` với token rỗng → nhận 401 → interceptor bắn toast lỗi + thử refresh. Tương tự với `nexoramobile://(app)/pricing`, `nexoramobile://(app)/resumes`.

### Tác động

- Không phải leak dữ liệu (backend vẫn chặn bằng 401), nhưng gây **crash tiềm ẩn** do component truy cập `currentUser.email` khi `currentUser` undefined, hiển thị skeleton/UI rác, và tạo vòng lặp refresh vô nghĩa.
- Reviewer mở deep link (họ có kiểm tra deep link) thấy UI lỗi → rủi ro reject.
- Defense-in-depth: authorization không nên chỉ ở một tầng.

### Sửa

[Phase 4, Bước 4.4](./03-REMEDIATION-PLAN.md).

---

## P1-05 · Access token lưu trong `localStorage` ở bản web

| | |
|---|---|
| **Mức** | 🟠 P1 (ảnh hưởng web build; `app.json` có `web.output: "static"`) |
| **Vị trí** | `src/services/storage.ts:11-29, 31-50` |

```
// src/services/storage.ts:12-21
if (Platform.OS === 'web') {
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      return window.localStorage.getItem(key);
    }
```

Access token **và** refresh token đều vào `localStorage` — đọc được bằng JavaScript, nên bất kỳ XSS nào (kể cả từ dependency bị compromise) đều lấy được token. Fallback thứ hai là `memoryStorage` (Map toàn cục) — cũng không an toàn hơn.

Không chặn release Play (đây là web) nhưng nếu web app cùng domain với marketing site thì rủi ro thật.

### Sửa

Web: giữ access token **chỉ trong memory**, refresh token trong cookie `HttpOnly; Secure; SameSite=Strict` do backend set. [Phase 4, Bước 4.5](./03-REMEDIATION-PLAN.md).

---

## P1-06 · Không có TLS pinning / Network Security Config

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | Thiếu ở `app.json`, `src/api/client.ts` |

App truyền CV, JD, lịch sử phỏng vấn, và bearer token qua HTTPS nhưng:
- Không có certificate/public-key pinning → dễ bị MITM trên thiết bị đã cài CA giả (máy root, mạng doanh nghiệp, proxy Burp/mitmproxy).
- Không có `networkSecurityConfig` tùy chỉnh để giới hạn trust anchor.
- `networkInspector` của expo-build-properties default là `true` → nếu không tắt ở production build sẽ để lộ khả năng inspect traffic.

Với ứng dụng xử lý CV (PII mức cao) đây là thiếu sót đáng kể, dù không phải điều kiện bắt buộc của Play.

### Sửa

[Phase 4, Bước 4.6](./03-REMEDIATION-PLAN.md) — cân nhắc pinning mức vừa (không pin quá cứng để tránh brick app khi backend đổi cert).

---

## P1-07 · Thông điệp lỗi thô từ backend được hiển thị cho người dùng

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `src/utils/errorTranslator.ts:26`, `src/api/client.ts:117-119` |

```
// src/utils/errorTranslator.ts — dòng cuối của translateErrorMessage
return englishMessage;    // ← fallback: trả NGUYÊN VĂN message từ backend
```

Message này được đưa thẳng vào toast:

```
// src/api/client.ts:117-119
} else if (extractedMessage) {
  toast.error(extractedMessage);
}
```

Hàm `translateErrorMessage` chỉ map ~18 pattern đã biết. Mọi lỗi ngoài danh sách (ví dụ exception ASP.NET chứa tên bảng, tên cột, đường dẫn file server, connection string bị truncate, stack trace) sẽ được **hiển thị nguyên văn cho người dùng**.

Comment trong code còn nói ngược lại thực tế:

```
// src/api/client.ts:105 — "displaying ONLY Vietnamese message, NO raw error codes"
```

### Tác động

Information disclosure (OWASP Mobile M10 / CWE-209). Kẻ tấn công có thể dùng app như oracle để map cấu trúc backend.

### Sửa

Allowlist: nếu không map được → hiện message chung; log mã lỗi + `requestId` để support tra. [Phase 4, Bước 4.7](./03-REMEDIATION-PLAN.md).

---

## P1-08 · Upload CV không giới hạn kích thước; PII tồn dư trong cache

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `src/app/(app)/resumes/index.tsx:45-63, 128-140` |

### 8a. Không validate size/type phía client

```
// src/app/(app)/resumes/index.tsx:46-51
mutationFn: async (file: DocumentPicker.DocumentPickerAsset) => {
  const intent = await resumesApi.presign({
    fileName: file.name,
    contentType: file.mimeType || 'application/pdf',   // ← mặc định giả định PDF
    size: file.size || 0,                              // ← nếu undefined thì gửi 0
  });
```

- Không có kiểm tra `file.size` trước khi upload → user chọn file 200MB thì app cố upload, tốn data 4G của họ, timeout, hoặc OOM.
- `file.mimeType || 'application/pdf'` — nếu picker không trả mimeType, client **khẳng định sai** với server rằng đây là PDF. Server phải tự sniff magic bytes; nhưng client không nên nói dối.
- `size: file.size || 0` — gửi 0 khi không biết, có thể lách kiểm tra size ở backend nếu backend tin giá trị này.

### 8b. File CV bị copy vào cache và không bao giờ dọn

```
// src/app/(app)/resumes/index.tsx:130-133
const result = await DocumentPicker.getDocumentAsync({
  type: ['application/pdf', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
  copyToCacheDirectory: true,     // ← bản sao CV nằm trong cache app
});
```

`copyToCacheDirectory: true` tạo bản sao CV trong `cacheDirectory`. Không có code nào xóa file này sau khi upload xong. Mỗi lần upload để lại một bản CV đầy đủ (tên, số điện thoại, địa chỉ, lịch sử công việc) trong thư mục cache — tồn tại đến khi Android tự dọn cache hoặc user xóa thủ công.

Lưu ý: `type` filter chỉ nhận PDF + DOCX nhưng người dùng vẫn có thể đổi tên file; filter của picker không phải kiểm soát bảo mật.

### 8c. `FileSystem.uploadAsync` bỏ qua interceptor

```
// src/app/(app)/resumes/index.tsx:53-58
const uploadResult = await FileSystem.uploadAsync(intent.uploadUrl, file.uri, {
  httpMethod: 'PUT',
  headers: { 'Content-Type': file.mimeType || 'application/pdf' },
});
if (uploadResult.status !== 200) {
  throw new Error('Upload to S3 failed');
}
```

Đúng về thiết kế (presigned URL không cần bearer token), nhưng: chỉ chấp nhận đúng status 200 — S3 PUT có thể trả **204**; và không có timeout/retry/progress. Nên kiểm `status >= 200 && status < 300`.

### Sửa

[Phase 4, Bước 4.8](./03-REMEDIATION-PLAN.md).

---

## P1-09 · Azure Speech token không được vô hiệu khi logout

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `src/services/speechTokenManager.ts:10, 47-55`, `src/context/auth-context.tsx:112-126` |

`speechTokenManager` giữ cache in-memory các token Azure Speech theo `interviewId`:

```
// src/services/speechTokenManager.ts:10
const cache = new Map<string, SpeechAuthorization>();
```

Đã có hàm dọn cache:

```
// src/services/speechTokenManager.ts:47
const clearInterviewSpeechAuthorizationCache = (interviewId?: string): void => {
```

Nhưng grep toàn repo: **`clearInterviewSpeechAuthorizationCache` không được gọi ở bất kỳ đâu**. `logoutAsync()` (`auth-context.tsx:112-126`) chỉ `clearTokens()` + `queryClient.clear()`.

### Tác động

Sau khi user A logout và user B login trên cùng thiết bị (cùng phiên tiến trình app), token Azure của user A vẫn nằm trong Map và vẫn hợp lệ cho tới khi hết hạn. Đây là credential của bên thứ ba có thể gọi trực tiếp `*.tts.speech.microsoft.com` — nếu bị đọc ra (qua memory dump hoặc bug khác) thì dùng được ngay.

Thiết kế token có `SPEECH_TOKEN_MIN_VALIDITY_MS = 60_000` và validate `expiresAt` rất tốt (`speechTokenManager.ts:71-92`) — chỉ thiếu bước dọn khi đổi phiên.

### Sửa

Gọi `clearInterviewSpeechAuthorizationCache()` trong `logoutAsync` và trong listener `onAuthError`. [Phase 4, Bước 4.9](./03-REMEDIATION-PLAN.md).

---

## P1-10 · `Math.random()` dùng làm fallback cho Idempotency-Key

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `src/api/client.ts:21-30` |

```
// src/api/client.ts:21-30
export function createIdempotencyKey(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;        // ← KHÔNG phải CSPRNG
    ...
```

Trên React Native/Hermes, `crypto.randomUUID` thường **không tồn tại** → luôn rơi vào fallback `Math.random()`. Hermes dùng PRNG không mật mã học, có thể dự đoán được.

Idempotency key của giao dịch thanh toán là **security-sensitive**: nếu đoán được key, kẻ tấn công có thể gây collision để chặn/replay giao dịch của người khác (mức độ phụ thuộc cách backend xử lý key trùng).

Lưu ý: `src/api/pricing.api.ts:12` lại dùng `react-native-uuid` (`uuid.v4()`) — tốt hơn. Có hai cơ chế sinh key không nhất quán trong cùng codebase.

### Sửa

Dùng `expo-crypto` (`Crypto.randomUUID()` / `getRandomBytesAsync`) và bỏ nhánh `Math.random()`. [Phase 4, Bước 4.10](./03-REMEDIATION-PLAN.md).

---

## P1-11 · Không có crash reporting / error monitoring thật

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `src/services/logger.ts:31-34`, `src/services/analytics.ts:22-26` |

```
// src/services/logger.ts:31-34
if (!__DEV__) {
  // In production builds, this feeds telemetry loggers like Sentry.captureException(error)
}
```

```
// src/services/analytics.ts:22-26
public recordError(error: Error | string, context?: string): void {
  if (__DEV__) {
    console.error(`[CrashReport] (${context || 'Global'})`, error);
  }
}    // ← ở production: KHÔNG LÀM GÌ
```

Hai "điểm nối" telemetry đều là placeholder. Ở production app **hoàn toàn mù**: không biết crash rate, không biết ANR, không biết lỗi API nào đang xảy ra với user thật.

### Tác động

- **Play Vitals**: Google đo crash rate / ANR rate và **giảm hiển thị trên Store** nếu vượt ngưỡng (bad behavior threshold). Không có telemetry = không thể phản ứng.
- Không có `ErrorBoundary` nào trong `src/app/_layout.tsx` → JS error không bắt được sẽ làm màn hình trắng/redbox.

### Sửa

Tích hợp Sentry (hoặc Firebase Crashlytics) + `ErrorBoundary` gốc + scrub PII trước khi gửi. [Phase 4, Bước 4.11](./03-REMEDIATION-PLAN.md).

---

## P1-12 · 14 lỗ hổng mức moderate trong cây phụ thuộc

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Bằng chứng** | `npm audit --omit=dev` → "14 moderate severity vulnerabilities" |

| Package | CVE/Advisory | Đường dẫn phụ thuộc |
|---|---|---|
| `decode-uri-component` ≤0.4.2 | [GHSA-vcc3-ghjq-m6fr](https://github.com/advisories/GHSA-vcc3-ghjq-m6fr) — DoS qua decode percent-encoding sai định dạng | `expo-router` → `query-string` → `decode-uri-component` |
| `uuid` <11.1.1 | [GHSA-w5hq-g745-h8pq](https://github.com/advisories/GHSA-w5hq-g745-h8pq) — thiếu kiểm tra biên buffer ở v3/v5/v6 | `expo` → `@expo/cli` → `@expo/config-plugins` → `xcode` → `uuid` |

**Đánh giá thực tế**: `uuid` nằm trong toolchain build (`@expo/cli`, `xcode`) → **không vào bundle runtime**, rủi ro thấp. `decode-uri-component` **có** vào runtime qua `expo-router` → cần theo dõi.

`npm audit fix --force` sẽ hạ `expo-router` xuống v5 (breaking) — **không được làm**. Cách xử lý đúng là chờ Expo patch hoặc dùng `overrides` có kiểm thử.

### Sửa

[Phase 5, Bước 5.4](./03-REMEDIATION-PLAN.md).

---

## P1-13 · Analytics tích lũy event vô hạn trong memory, không có consent

| | |
|---|---|
| **Mức** | 🟠 P1 |
| **Vị trí** | `src/services/analytics.ts:8, 10-20` |

```
// src/services/analytics.ts:8-19
private events: AnalyticsEvent[] = [];

public logEvent(name: string, params?: Record<string, any>): void {
  const event: AnalyticsEvent = { name, params, timestamp: new Date().toISOString() };
  this.events.push(event);      // ← không giới hạn, không flush, không xóa
```

- Mảng `events` chỉ tăng, không bao giờ được đọc hay xóa → memory leak dần trong session dài (phiên phỏng vấn có thể 30–60 phút).
- `params: Record<string, any>` không có schema → dev rất dễ log PII (email, nội dung câu trả lời) vào đây.
- Không có cơ chế **consent** cho analytics — nếu sau này nối vào SDK thật (Firebase/Amplitude) mà chưa có consent thì vi phạm User Data policy và GDPR.

So sánh: `signalRService` xử lý bounded set đúng cách (`src/services/signalr.ts:23-26` — giới hạn 200 phần tử). Analytics nên theo mẫu đó.

### Sửa

[Phase 4, Bước 4.12](./03-REMEDIATION-PLAN.md).

---

## P1-14 · `withCredentials: true` cho mọi request, không có chống CSRF

| | |
|---|---|
| **Mức** | 🟠 P1 (chủ yếu ảnh hưởng web build) |
| **Vị trí** | `src/api/client.ts:11-18`, `src/context/auth-context.tsx:31-41` |

```
// src/api/client.ts:11-18
export const apiClient = axios.create({
  baseURL: API_BASE_URL,
  withCredentials: true,     // ← gửi cookie cho MỌI request, mọi endpoint
```

Kiến trúc hiện tại là **hybrid**: bearer token trong header **và** cookie refresh token — kiểu kết hợp này là nguồn gốc kinh điển của lỗ hổng CSRF trên web, vì trình duyệt tự gắn cookie vào request cross-site.

Cần (ở backend): cookie `SameSite=Strict` + `HttpOnly` + `Secure`, CORS allowlist chặt (không `*` khi `withCredentials`), và endpoint `/auth/refresh` chỉ chấp nhận POST với kiểm tra origin.

Trên native, `withCredentials` phần lớn vô nghĩa nhưng vẫn khiến cookie jar của OkHttp tham gia — liên quan trực tiếp tới P1-03.

### Sửa

[Phase 4, Bước 4.3](./03-REMEDIATION-PLAN.md) (gộp với refresh flow).

---

# PHẦN C — P2: MEDIUM

---

## P2-01 · SignalR được khai báo nhưng package chưa được cài → realtime chết, thay bằng polling 3s

**Vị trí**: `src/services/signalr.ts` (toàn file), `package.json` (thiếu `@microsoft/signalr`)

`SignalRService` chỉ có 2 hàm: `isNewEvent()` (dedupe) và `getHubUrl()`. **Không có** `HubConnectionBuilder`, không có kết nối thật, và `@microsoft/signalr` không có trong dependencies.

Hệ quả: app bù bằng polling liên tục:

```
// src/components/interview/useInterviewSession.ts:48-60 (rút gọn)
refetchInterval: (query) => {
  ... if (status === 'starting' || 'completing' || 'evaluating' || reportState === 'processing' ...)
```

```
// src/app/(app)/interview/report/[id].tsx:95
return 3000;      // ← poll mỗi 3 giây
```

**Tác động**: tiêu thụ pin + data của user trong suốt quá trình chờ AI xử lý; tăng tải backend (đang ở Render free tier). Không phải vi phạm policy nhưng ảnh hưởng chất lượng và có thể bị nêu trong Play Vitals (excessive wakeups/network).

---

## P2-02 · Dependency không sử dụng trong bundle

| Package | Số file dùng | Ghi chú |
|---|---|---|
| `lottie-react-native` | **0** | Không dùng ở đâu |
| `@lottiefiles/dotlottie-react` | **0** | Thư viện **web-only** (React DOM) trong app RN |
| `expo-linking` | **0** | Không dùng, dù app có `scheme` |
| `expo-constants` | **0** | Không dùng |

Mỗi dependency không dùng là bề mặt supply-chain thừa + tăng kích thước bundle. `@lottiefiles/dotlottie-react` đặc biệt đáng lo: nó kéo theo dependency DOM vào Metro graph.

---

## P2-03 · File rác 600KB+ được commit vào repo

- `react_doctor.json` — **472 KB**
- `react_doctor_utf8.json` — **202 KB**

Đây là output của công cụ chẩn đoán, không nên track. Cần kiểm tra chúng có chứa đường dẫn máy local / thông tin môi trường dev không, rồi xóa + thêm vào `.gitignore`.

---

## P2-04 · Chưa xác minh tuân thủ 16 KB page size

Yêu cầu Google Play: từ **01-11-2025**, mọi app mới và bản cập nhật targeting Android 15+ phải hỗ trợ 16 KB page size. Ảnh hưởng tới app có native code.

Nexora có **nhiều** native library qua: `react-native-reanimated` 4.5.1, `react-native-screens`, `react-native-svg`, `react-native-gesture-handler`, `react-native-worklets`, `expo-secure-store`, `lottie-react-native`, Hermes.

Expo SDK 57 / RN 0.86 đã dùng NDK r28+ nên **về lý thuyết đã tuân thủ**, nhưng **phải verify trên bản AAB thật** — Play Console sẽ chặn upload nếu không đạt.

Cách kiểm: dùng `check_elf_alignment.sh` của Android trên AAB đã build, hoặc test trên emulator Android 15 với system image 16 KB.

---

## P2-05 · `versionCode` cố định trong `app.json` xung đột với `appVersionSource: "remote"`

```
// eas.json:4
"appVersionSource": "remote"
// app.json:19
"versionCode": 1
```

Khi `appVersionSource` là `remote`, EAS quản lý version — giá trị trong `app.json` bị bỏ qua và gây nhầm lẫn. Tương tự `ios.buildNumber: "1.0.0"` (`app.json:13`) sai định dạng: buildNumber nên là số nguyên tăng dần, không phải semver.

---

## P2-06 · Chưa cấu hình `expo-updates` / OTA policy

Không có `expo-updates` trong dependencies, không có khối `updates` hay `runtimeVersion` trong `app.json`. Nếu sau này bật OTA cần lưu ý: Google cho phép cập nhật JS bundle nhưng **không được thay đổi mục đích/chức năng chính của app** so với bản đã review — cần policy rõ ràng trước khi bật.

---

## P2-07 · Hạ tầng test gần như không có

- `package.json` **không có** script `test` và không có test runner (jest / vitest).
- Chỉ có 1 file: `tests/authParity.test.mjs` — và nó là **structural test** (đọc source code rồi regex match), không phải test hành vi:

```
// tests/authParity.test.mjs:31-33
assert.match(loginSrc, /if \(!\/\[A-Z\]\/\.test\(pass\)\)/, 'Password validation must check for uppercase letters');
```

Loại test này vỡ ngay khi refactor (đổi thứ tự điều kiện, đổi regex tương đương) mà không phát hiện được lỗi logic thật.

---

## P2-08 · Thiếu `android.blockedPermissions`

`app.json` không có `blockedPermissions`. Các thư viện native (hiện tại hoặc thêm sau) có thể tự merge quyền vào manifest mà team không biết — ví dụ `READ_MEDIA_IMAGES`, `CAMERA`, `ACCESS_NETWORK_STATE`, `VIBRATE`. Quyền lạ xuất hiện trong Play Store listing → phải giải trình với reviewer.

Cần khai báo `blockedPermissions` tường minh và kiểm `AndroidManifest.xml` sau prebuild.

---

## P2-09 · Thiếu bảo vệ chống chụp/quay màn hình trên màn chứa PII

Màn hình CV (`resumes/index.tsx`), phân tích CV (`cv-analysis/[id].tsx`), báo cáo phỏng vấn (`interview/report/[id].tsx`) hiển thị PII và kết quả đánh giá. Không có `FLAG_SECURE` / `expo-screen-capture`.

Không bắt buộc bởi Play, nhưng là kỳ vọng hợp lý với app xử lý CV — cân nhắc bật cho màn CV.

---

## P2-10 · `INTERNET` khai báo thủ công (không cần thiết)

`app.json:23` khai `"INTERNET"`. Expo/RN **tự động** thêm quyền này. Khai thủ công không sai nhưng làm danh sách quyền khó audit — nên bỏ để `permissions` chỉ chứa quyền có chủ đích.

---

## P2-11 · `ios.icon` trỏ tới đường dẫn khả nghi

```
// app.json:13
"icon": "./assets/expo.icon",
```

`assets/expo.icon/` là một **thư mục** (chứa `icon.json`), không phải file ảnh. Trong khi `expo.icon` cấp cao hơn (`app.json:6`) lại trỏ `./assets/images/icon.png`. Cần xác minh cấu hình này build được trên iOS — hiện chỉ nhắm Android nên chưa lộ ra.

---

## P2-12 · Icon 799 KB

`assets/images/icon.png` = **799 KB**, `logo-glow.png` = 331 KB. Icon app nên dưới ~100 KB. Ảnh hưởng kích thước tải xuống — chỉ số Google đo và hiển thị trên Store.

---

## P2-13 · Nội dung pháp lý có mâu thuẫn nội tại về độ tuổi

```
// src/components/ui/legal-policy-modal.tsx:359
"dành cho người dùng từ 18 tuổi trở lên (hoặc từ 13 tuổi với sự giám sát của người giám hộ).
 Chúng tôi không chủ động thu thập thông tin cá nhân của trẻ em dưới 13 tuổi."
```

Phát biểu này mơ hồ: vừa nói 18+, vừa cho 13+. Điều này **phải khớp** với câu trả lời trong **Content Rating questionnaire** và **Target Audience** trên Play Console. Nếu khai "18+" trên Console nhưng chính sách nói 13+ → reviewer thấy mâu thuẫn. Nếu khai target audience gồm trẻ em → kích hoạt **Families policy** với yêu cầu khắt khe hơn nhiều.

---

# PHẦN D — P3: LOW / Backlog

| ID | Nội dung | Vị trí |
|---|---|---|
| P3-01 | `axios` timeout 30s có thể quá ngắn cho cold start Render free tier → user thấy "Không thể kết nối máy chủ" | `src/api/client.ts:17` |
| P3-02 | `processedEventIds` dedupe set xóa phần tử cũ nhất theo thứ tự insert — logic đúng nhưng nên dùng LRU thật nếu event burst | `src/services/signalr.ts:23-26` |
| P3-03 | `AppError` bọc nguyên `AxiosError` gốc (`cause`) → nếu error này lọt vào crash report sẽ mang theo header Authorization | `src/api/types.ts`, `src/api/client.ts:90` |
| P3-04 | `speech.ts` `onend` auto-restart recognition vô hạn (`speech.ts:160-176`) — trên web có thể gây loop nếu mic bị rút | `src/services/speech.ts:160-176` |
| P3-05 | `metro.config.js` là config default — chưa cấu hình `resolver.blockList` để loại file test/doc khỏi bundle | `metro.config.js` |
| P3-06 | `tsconfig.json` có `strict: true` (tốt) nhưng nhiều nơi dùng `as any` để lách (`router.replace(... as any)`) — làm mất giá trị của strict mode | nhiều file trong `src/app/` |

---

# PHẦN E — Ma trận đối chiếu OWASP Mobile Top 10 (2024)

| # | Hạng mục | Trạng thái | Finding liên quan |
|---|---|---|---|
| M1 | Improper Credential Usage | ⚠️ Có vấn đề | P1-03 (refresh token vô dụng), P1-09 (Azure token không dọn) |
| M2 | Inadequate Supply Chain Security | ⚠️ Có vấn đề | P1-12 (14 CVE), P2-02 (dep chết) |
| M3 | Insecure Authentication/Authorization | ⚠️ Có vấn đề | P1-04 (thiếu guard `(app)`), P1-14 (CSRF hybrid auth) |
| M4 | Insufficient Input/Output Validation | ⚠️ Có vấn đề | P1-08 (không validate file) |
| M5 | Insecure Communication | ⚠️ Có vấn đề | P1-06 (không pinning) — nhưng HTTPS-only là điểm tốt |
| M6 | Inadequate Privacy Controls | 🔴 Nghiêm trọng | P0-05, P0-07, P0-08, P1-13 |
| M7 | Insufficient Binary Protections | 🔴 Nghiêm trọng | P1-02 (không R8/minify) |
| M8 | Security Misconfiguration | ⚠️ Có vấn đề | P0-02 (quyền thừa), P2-08 (thiếu blockedPermissions) |
| M9 | Insecure Data Storage | ⚠️ Có vấn đề | P1-05 (localStorage web), P1-08b (cache PII) |
| M10 | Insufficient Cryptography | ⚠️ Có vấn đề | P1-10 (`Math.random()`) |

---

# PHẦN F — Bảng tra nhanh: finding theo file

| File | Finding |
|---|---|
| `app.json` | P0-02, P1-02, P2-05, P2-08, P2-10, P2-11 |
| `eas.json` | P2-05 |
| `package.json` | P1-02, P1-12, P2-01, P2-02, P2-07 |
| `src/api/client.ts` | P1-01, P1-07, P1-10, P1-14, P3-01, P3-03 |
| `src/api/pricing.api.ts` | P0-01 |
| `src/api/user.api.ts` | P0-07 |
| `src/app/(app)/_layout.tsx` | P1-04 |
| `src/app/(app)/account/index.tsx` | P0-05, P0-06 |
| `src/app/(app)/pricing/index.tsx` | P0-01 |
| `src/app/(app)/resumes/index.tsx` | P1-01, P1-08 |
| `src/app/(app)/interview/preflight.tsx` | P0-02 |
| `src/app/(app)/interview/report/[id].tsx` | P0-04, P2-01, P2-09 |
| `src/components/ui/legal-policy-modal.tsx` | P0-08, P2-13 |
| `src/context/auth-context.tsx` | P1-03, P1-09 |
| `src/services/analytics.ts` | P1-11, P1-13 |
| `src/services/logger.ts` | P1-01, P1-11 |
| `src/services/signalr.ts` | P2-01, P3-02 |
| `src/services/speech.ts` | P0-02, P0-03, P1-01, P3-04 |
| `src/services/speechTokenManager.ts` | P1-09 |
| `src/services/storage.ts` | P1-01, P1-05 |
| `src/services/tts.ts` | P0-03, P1-01 |
| `src/utils/errorTranslator.ts` | P1-07 |
| `react_doctor*.json` | P2-03 |
