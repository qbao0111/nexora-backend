# 03 — Kế hoạch sửa lỗi chi tiết (step-by-step)

> **Tài liệu này là kế hoạch thi công.** Mỗi bước có: mục tiêu, file đích, việc phải làm, cạm bẫy, và **acceptance criteria** (điều kiện đóng bước).
>
> **Quy tắc bắt buộc**: trước khi dùng bất kỳ API Expo nào, tra đúng doc phiên bản **https://docs.expo.dev/versions/v57.0.0/** (yêu cầu của `AGENTS.md`). Tên option đã đổi giữa các SDK — ví dụ `enableProguardInReleaseBuilds` (cũ) → **`enableMinifyInReleaseBuilds`** (SDK 57).

---

## Sơ đồ phụ thuộc giữa các phase

```
Phase 0 (Chuẩn bị)
   │
   ├──────────────┬──────────────┬──────────────┐
   ▼              ▼              ▼              ▼
Phase 1        Phase 2        Phase 3        Phase 4
Broken         Payments       Pháp lý &      Bảo mật
functionality  (cần backend)  dữ liệu        (P1)
   │              │              │              │
   └──────────────┴──────────────┴──────────────┘
                         │
                         ▼
                  Phase 5 (Hardening + Test + Submit)
```

Phase 1–4 **chạy song song được** nếu có đủ người. Phase 0 phải xong trước. Phase 5 phải cuối.

**Đường găng (critical path)**: Phase 2 — backend phải làm endpoint verify Google Play purchase token. Kick-off ngay ngày đầu.

---

# PHASE 0 — Chuẩn bị (2 người-ngày)

## Bước 0.1 · Tạo nhánh làm việc và cấu trúc theo dõi

**Việc làm**
1. Từ nhánh `tester` (nhánh chứa tài liệu audit này), tạo nhánh thi công: `git checkout -b fix/play-compliance-p0`.
2. Tạo thư mục `docs/release-audit/evidence/` để lưu bằng chứng verify từng finding (screenshot, log, output command).
3. Tạo issue tracker: mỗi finding P0/P1 là 1 issue, label theo `P0`/`P1`/`P2`, gắn với bước tương ứng trong tài liệu này.

**Acceptance**: có branch, có thư mục evidence, có 22 issue (8 P0 + 14 P1) được tạo và assign.

---

## Bước 0.2 · Tách môi trường dev / staging / production

**Vấn đề hiện tại**

```
// src/api/client.ts:8-9
export const API_BASE_URL =
  process.env.EXPO_PUBLIC_API_URL || 'https://nexora-backend-q32b.onrender.com/api/v1';
```

Fallback hardcode URL production. Nếu `EXPO_PUBLIC_API_URL` bị quên khi build, app production lặng lẽ dùng đúng URL — nhưng cũng có nghĩa là mọi bản dev/test đều trỏ vào production nếu không set env.

**Việc làm**
1. Tạo `.env.development`, `.env.staging`, `.env.production` (đã được `.gitignore` che — xác nhận lại `.gitignore` dòng `.env*`).
2. Tạo `.env.example` **được commit**, chứa tên biến + giá trị giả:
   ```
   EXPO_PUBLIC_API_URL=https://your-backend.example.com/api/v1
   EXPO_PUBLIC_ENV=development
   EXPO_PUBLIC_SENTRY_DSN=
   ```
3. Trong `eas.json`, thêm khối `env` cho từng profile (`development`, `preview`, `production`) trỏ đúng backend.
4. Bỏ fallback hardcode: nếu `EXPO_PUBLIC_API_URL` không tồn tại → **throw ngay khi khởi động** ở dev, để lỗi cấu hình lộ ra sớm thay vì âm thầm.

**Cạm bẫy**: `EXPO_PUBLIC_*` được **inline vào bundle** lúc build — đây là biến công khai, **không bao giờ** đặt secret vào đó.

**Acceptance**: build dev trỏ staging, build production trỏ production, `.env.example` có trong repo, không còn URL production hardcode trong `src/`.

---

## Bước 0.3 · Dựng development build trên thiết bị Android thật

**Lý do**: toàn bộ Phase 1 liên quan native module (audio, image picker, IAP) — **không thể test trên Expo Go**. Đây là điều kiện tiên quyết.

**Việc làm**
1. `npx expo install expo-build-properties` (sẽ dùng ở Phase 5, cài sớm để prebuild ổn định).
2. `eas build --profile development --platform android` → cài APK lên máy thật.
3. Xác nhận app chạy, login được, gọi API staging thành công.
4. Ghi lại model + Android version của máy test vào `docs/release-audit/evidence/device-matrix.md`.

**Acceptance**: có dev build chạy trên ít nhất 1 máy Android thật (khuyến nghị: 1 máy Android 13, 1 máy Android 15).

---

# PHASE 1 — Sửa tính năng hỏng (P0-02, P0-03, P0-05, P0-06) · 10–14 người-ngày

> ⚠️ **Quyết định kiến trúc bắt buộc trước khi bắt đầu.**
>
> Tính năng voice (STT + TTS) trên native cần 5–8 ngày. Hai lựa chọn:
>
> | Lựa chọn | Effort | Rủi ro | Khi nào chọn |
> |---|---|---|---|
> | **A — Làm voice native đầy đủ** | 5–8 ngày | Trung bình (STT tiếng Việt trên Android cần dịch vụ ngoài) | Nếu voice là giá trị cốt lõi không thể cắt |
> | **B — Cắt voice khỏi v1, chỉ text** | 1–2 ngày | Thấp | Nếu cần ra bản đầu nhanh |
>
> **Khuyến nghị**: chọn **B cho v1**, làm A cho v1.1. Lý do: đóng được cả P0-02 và P0-03 trong 2 ngày thay vì 2 tuần, và loại bỏ hoàn toàn rủi ro "unnecessary permission".
>
> Các bước dưới đây trình bày **cả hai đường**.

---

## Bước 1.1 · [ĐƯỜNG B] Cắt tính năng voice khỏi bản v1

**Mục tiêu**: đóng P0-02 + P0-03 bằng cách loại bỏ tính năng không hoạt động thay vì để nó hỏng.

**Việc làm**

1. **`app.json`** — xóa quyền âm thanh:
   - Xóa `"RECORD_AUDIO"` và `"MODIFY_AUDIO_SETTINGS"` khỏi `android.permissions`.
   - Xóa `"INTERNET"` luôn (Expo tự thêm) → `android.permissions` trở thành `[]`.
   - Xóa `ios.infoPlist.NSMicrophoneUsageDescription`.

2. **`src/app/(app)/interview/preflight.tsx`**:
   - Xóa toàn bộ component mic-test (khoảng dòng 680–940): `micStatus`, `handleTest`, `meterLevel`, `setMeterLevel`, UI đo tín hiệu.
   - Xóa dòng 691 (`Math.random()` meter).
   - Đổi `const [micMode, setMicMode] = useState<'voice' | 'text'>('voice')` (dòng 1032) → cố định `'text'`, xóa UI chọn mode.
   - Sửa dòng 1130: bỏ query param `micMode` hoặc hardcode `micMode=text`.
   - Sửa text hero banner dòng 106: bỏ "và kiểm tra microphone trước khi bắt đầu".

3. **`src/app/(app)/interview/[id].tsx`** + **`src/components/interview/useInterviewSession.ts`**:
   - Xóa import + mọi lệnh gọi `speechService` và `ttsService`.
   - Xóa state `isRecording`, `isTtsSpeaking`, `isMicAllowed`, `checkMicPermission` (dòng 27-42).
   - Xóa `useEffect` gọi `ttsService.speak` (dòng 93, 117).

4. **`src/components/interview/AudioSpeechDock.tsx`**:
   - Chuyển thành dock chỉ có nhập text, hoặc xóa file và thay bằng `TextInput` đơn giản trong `interview/[id].tsx`.

5. **Xóa file không còn dùng**: `src/services/speech.ts`, `src/services/tts.ts`, `src/services/speechApi.ts`, `src/services/speechTokenManager.ts`, `src/config/speech.ts`.
   - ⚠️ Việc xóa `speechTokenManager.ts` cũng đóng luôn **P1-09**.

6. **`src/components/ui/legal-policy-modal.tsx`**:
   - Xóa section 4 "Quyền truy cập thiết bị & ghi âm" (dòng ~349-355).
   - Xóa bullet "Giọng nói & Âm thanh" (dòng ~336).

7. **`package.json`**: không cần thêm gì; xác nhận không còn import nào tới file đã xóa (`npx tsc --noEmit`).

**Cạm bẫy**
- Nhớ kiểm `src/components/interview/InterviewSubComponents.tsx` và `AiInterviewerPresence.tsx` xem có phụ thuộc `isTtsSpeaking` không.
- Store listing + screenshot **không được** quảng cáo tính năng voice.

**Acceptance**
- [ ] `npx tsc --noEmit` pass, `npm run lint` pass.
- [ ] `grep -rn "RECORD_AUDIO\|MODIFY_AUDIO" app.json` → 0 kết quả.
- [ ] `grep -rn "speechService\|ttsService" src/` → 0 kết quả.
- [ ] Sau `expo prebuild`, `android/app/src/main/AndroidManifest.xml` **không** chứa `RECORD_AUDIO`.
- [ ] Chạy trên máy thật: hoàn thành 1 phiên phỏng vấn đầy đủ bằng text, nhận được báo cáo.
- [ ] Screenshot lưu vào `evidence/P0-02-no-mic-permission.png` và `evidence/P0-03-text-interview-works.png`.

---

## Bước 1.2 · [ĐƯỜNG A] Triển khai ghi âm native bằng `expo-audio`

> Chỉ làm bước này nếu chọn Đường A. Nếu chọn Đường B, bỏ qua tới Bước 1.5.

**Doc tham chiếu**: https://docs.expo.dev/versions/v57.0.0/sdk/audio/

**Việc làm**

1. `npx expo install expo-audio`

2. Cấu hình config plugin trong `app.json` — **để plugin tự thêm quyền**, không khai thủ công:
   ```json
   [
     "expo-audio",
     {
       "microphonePermission": "Nexora cần quyền truy cập microphone để bạn thực hành phỏng vấn qua giọng nói.",
       "recordAudioAndroid": true,
       "enableBackgroundRecording": false
     }
   ]
   ```
   Doc SDK 57 nêu rõ: `recordAudioAndroid` (default `true`) "determines whether to enable the `RECORD_AUDIO` permission on Android" — plugin **tự thêm quyền lúc build**.
   → Sau đó **xóa `RECORD_AUDIO` và `MODIFY_AUDIO_SETTINGS` khỏi `android.permissions`** để tránh khai trùng/khai thừa.
   → `enableBackgroundRecording: false` là bắt buộc — khớp với cam kết trong chính sách "không ghi âm ngầm dưới nền".

3. Viết lại `src/services/speech.ts` thành interface có 2 implementation:
   - `speech.web.ts` — giữ code Web Speech API hiện tại.
   - `speech.native.ts` — dùng `useAudioRecorder(RecordingPresets.HIGH_QUALITY)`, ghi ra file, upload lên backend để STT (Azure Speech STT hoặc Whisper — **phải thống nhất với backend team**).
   - Metro tự resolve theo platform suffix.

4. API cần dùng (đúng SDK 57):
   ```
   import {
     useAudioRecorder, useAudioRecorderState, AudioModule,
     RecordingPresets, setAudioModeAsync, requestRecordingPermissionsAsync,
   } from 'expo-audio';
   ```
   - Xin quyền: `await requestRecordingPermissionsAsync()` → kiểm `granted`.
   - Kiểm quyền không xin: `getRecordingPermissionsAsync()`.
   - Ghi: `await recorder.prepareToRecordAsync(); recorder.record();`
   - Dừng: `await recorder.stop();` rồi đọc `recorder.uri`.

5. **Dọn file ghi âm**: file mặc định vào cache directory. Phải xóa bằng `FileSystem.deleteAsync` **ngay sau khi upload xong** — giọng nói là dữ liệu sinh trắc học, không để tồn dư.

**Cạm bẫy**
- `RecordingPresets.HIGH_QUALITY` = 44.1kHz / 128kbps / `.m4a`. Xác nhận backend STT nhận được `.m4a`; nếu cần `.wav` thì phải custom options.
- Không bật `enableBackgroundRecording` — vừa vi phạm cam kết chính sách vừa cần quyền thêm.
- Xin quyền phải có **rationale UI** trước khi gọi dialog hệ thống (giải thích tại sao cần mic) — là best practice Android và giảm tỉ lệ từ chối.

**Acceptance**
- [ ] Trên máy thật: bấm "Kiểm tra microphone" → hiện dialog quyền hệ thống Android thật.
- [ ] Từ chối quyền → app chuyển sang mode text, không crash, thông báo rõ ràng.
- [ ] Cấp quyền → thanh đo hiển thị **mức âm thanh thật** (nói to/nhỏ thấy khác nhau).
- [ ] Ghi âm 10 giây → upload → nhận transcript đúng tiếng Việt.
- [ ] File ghi âm bị xóa khỏi cache sau upload (verify bằng `FileSystem.getInfoAsync`).
- [ ] Video quay màn hình lưu vào `evidence/P0-02-real-mic-permission.mp4`.

---

## Bước 1.3 · [ĐƯỜNG A] Triển khai TTS native

**Việc làm**

1. Tách `src/services/tts.ts` thành `tts.web.ts` (giữ nguyên) + `tts.native.ts`.

2. `tts.native.ts` — hai phương án:
   - **Đơn giản**: dùng `expo-speech` (có trong SDK 57) với `Speech.speak(text, { language: 'vi-VN' })`. Dùng giọng TTS của hệ thống — chất lượng thấp hơn nhưng offline, không cần token, không phụ thuộc Azure. **Khuyến nghị cho v1.**
   - **Chất lượng cao**: giữ Azure TTS REST, nhưng thay `URL.createObjectURL` + `new Audio()` bằng:
     - Lưu response thành file qua `FileSystem.writeAsStringAsync` (base64) hoặc `FileSystem.downloadAsync`.
     - Phát bằng `useAudioPlayer` của `expo-audio`.
     - Xóa file sau khi phát xong.

3. **Sửa lỗi giọng đọc sai ngôn ngữ**: `src/config/speech.ts:2` đang là `de-DE-Seraphina:DragonHDLatestNeural` — **giọng tiếng Đức**. Đổi sang giọng tiếng Việt (`vi-VN-HoaiMyNeural` hoặc `vi-VN-NamMinhNeural`), khớp với `xml:lang='vi-VN'` trong SSML.

4. Giữ nguyên `escapeXml()` (`tts.ts:5-22`) — code này đúng, chống SSML injection.

**Acceptance**
- [ ] Trên máy thật: AI interviewer đọc câu hỏi thành tiếng, bằng **tiếng Việt**.
- [ ] Bấm dừng → âm thanh ngắt ngay.
- [ ] Rời màn hình giữa lúc đang đọc → âm thanh dừng, không leak.
- [ ] Không còn file audio tạm tồn trong cache sau khi phát.

---

## Bước 1.4 · [ĐƯỜNG A] Thay mic-test giả bằng đo tín hiệu thật

**Việc làm**

Sửa `src/app/(app)/interview/preflight.tsx:703-721`:

1. Xóa nhánh `setTimeout(() => setMicStatus('ready'), 1200)` — **tuyệt đối không** giả lập thành công.
2. Trên native: gọi `requestRecordingPermissionsAsync()`, nếu `granted` thì `prepareToRecordAsync()` + `record()` trong ~3 giây và đọc mức âm thanh từ `useAudioRecorderState(recorder)`, rồi `stop()`.
3. Xóa dòng 691 (`setMeterLevel(Math.floor(Math.random() * 60) + 30)`) — thay bằng `metering` thật.
4. Nếu không có mic hoặc từ chối quyền → `micStatus = 'blocked'` + chuyển sang mode text (logic này đã đúng, giữ lại).

**Acceptance**
- [ ] `grep -n "Math.random" src/app/\(app\)/interview/preflight.tsx` → 0 kết quả.
- [ ] Bịt micro bằng tay → thanh đo gần 0. Nói to → thanh đo lên cao.
- [ ] Trên emulator không có mic → hiện `blocked`, không hiện `ready`.

---

## Bước 1.5 · Sửa "Xuất dữ liệu cá nhân" (P0-05)

**File**: `src/app/(app)/account/index.tsx:155-169`

**Việc làm**

1. `npx expo install expo-file-system expo-sharing`
2. Viết lại `handleExportData()`:
   - Gọi `userApi.exportData()`.
   - `JSON.stringify(data, null, 2)` → ghi ra `${FileSystem.documentDirectory}nexora-data-export-${YYYYMMDD-HHmmss}.json` bằng `writeAsStringAsync`.
   - Gọi `Sharing.shareAsync(fileUri, { mimeType: 'application/json', dialogTitle: 'Lưu dữ liệu cá nhân Nexora' })` để user chọn nơi lưu / gửi đi.
   - Kiểm `await Sharing.isAvailableAsync()` trước; nếu không khả dụng → hiện đường dẫn file và hướng dẫn.
3. **Chỉ hiện Alert thành công SAU KHI** file được ghi và share dialog đã mở — không hiện trước.
4. Thêm cảnh báo trong UI: "Tệp chứa dữ liệu cá nhân của bạn. Hãy lưu ở nơi an toàn."
5. **Xóa file export sau khi share xong** (hoặc sau một khoảng thời gian) — không để bản sao PII đầy đủ nằm mãi trong `documentDirectory`.

**Cạm bẫy**
- `documentDirectory` được backup lên Google Drive theo mặc định → nếu để file PII ở đó vĩnh viễn thì PII lên cloud backup. Dùng `cacheDirectory` + xóa ngay, hoặc thêm `android:allowBackup` exclusion.
- Nếu data export rất lớn, `JSON.stringify` có thể gây OOM → cân nhắc để backend trả về presigned download URL thay vì JSON inline.

**Acceptance**
- [ ] Bấm "Xuất dữ liệu" → share sheet Android mở ra với file `.json` thật.
- [ ] Mở file → thấy đúng dữ liệu của tài khoản đang đăng nhập.
- [ ] Không còn Alert nào nói "thành công" khi chưa có file.
- [ ] File tạm được xóa sau khi hoàn tất.
- [ ] Screenshot lưu `evidence/P0-05-export-works.png`.

---

## Bước 1.6 · Sửa hoặc ẩn nút ảnh đại diện (P0-06)

**File**: `src/app/(app)/account/index.tsx:252, 260`

### Lựa chọn A — Triển khai thật (nếu backend có endpoint avatar)

1. `npx expo install expo-image-picker`
2. `app.json`: thêm config plugin `expo-image-picker` với `photosPermission` (chuỗi tiếng Việt). Không khai `READ_MEDIA_IMAGES` thủ công — để plugin tự thêm.
3. Nút "Đổi ảnh": `ImagePicker.launchImageLibraryAsync({ mediaTypes: 'images', allowsEditing: true, aspect: [1,1], quality: 0.8 })` → resize → upload (theo pattern presign giống CV) → invalidate `['currentUser']`.
4. Nút "Gỡ ảnh": `Alert.alert` xác nhận → `DELETE /me/avatar` → invalidate query. **Chỉ hiện "Đã gỡ" khi API trả thành công.**
5. Giới hạn: ảnh ≤ 5MB, chỉ JPEG/PNG/WebP.

### Lựa chọn B — Ẩn nút (khuyến nghị cho v1)

1. Xóa hoàn toàn 2 `TouchableOpacity` ở dòng 252 và 260 khỏi JSX.
2. Giữ `UserAvatar` chỉ để **hiển thị** (dùng chữ cái đầu / ảnh từ backend nếu có).
3. **Không** để lại nút bị `disabled` — reviewer vẫn coi đó là tính năng chưa hoàn thiện.

**Acceptance**
- [ ] `grep -rn "Alert.alert('Thông báo'" src/` → **0 kết quả**.
- [ ] Không có nút nào trong toàn app khi bấm chỉ hiện Alert mô tả mà không làm gì.
- [ ] (Nếu chọn A) Đổi ảnh thật → ảnh mới hiện sau khi reload app.

---

## Bước 1.7 · Rà soát toàn bộ app tìm nút chết còn lại

**Việc làm**

1. Chạy checklist rà soát thủ công: mở **từng** màn hình trên máy thật, bấm **từng** phần tử có thể tương tác, ghi nhận phần tử nào không tạo ra thay đổi nào.
2. Grep các pattern nghi vấn:
   ```
   grep -rn "onPress={() =>" src/ | grep -i "alert\|console\|//"
   grep -rn "TODO\|FIXME\|chưa khả dụng\|coming soon" src/
   grep -rn "disabled={true}\|disabled\b" src/app/
   ```
3. Lập bảng trong `docs/release-audit/evidence/dead-button-audit.md`: màn hình / phần tử / kết quả / hành động xử lý.

**Acceptance**: bảng rà soát đầy đủ 100% màn hình (25 route trong `src/app/`), mọi phần tử đều có hành vi thật hoặc đã bị ẩn.

---

# PHASE 2 — Chuyển sang Google Play Billing (P0-01) · 6–9 người-ngày

> 🔴 **Đây là đường găng.** Backend phải làm song song. Nếu backend chưa sẵn sàng, xem Phụ lục B (cắt tính năng thanh toán khỏi v1).

## Bước 2.0 · Quyết định business trước khi viết code

**Phải chốt với stakeholder:**

| Câu hỏi | Ảnh hưởng |
|---|---|
| Gói PRO là **one-time purchase** hay **subscription tự gia hạn**? | Quyết định loại product trên Play Console, và có phải tuân Subscriptions policy hay không |
| Chấp nhận phí Google 15–30%? Có tăng giá bù không? | `amountMinor` trong `PlanPriceResponseV2` phải tính lại |
| Web app có tiếp tục dùng cổng thanh toán VN không? | Nếu có → user mua trên web, entitlement dùng được trên mobile (được phép), nhưng **app Android không được quảng bá/dẫn tới web checkout** |
| Xử lý user đã mua qua cổng ngoài trước đây thế nào? | Cần migration plan cho entitlement cũ |

**Acceptance**: có tài liệu quyết định được stakeholder ký, lưu `docs/release-audit/decisions/payments.md`.

---

## Bước 2.1 · Chọn và cài thư viện IAP

**Doc tham chiếu**: https://docs.expo.dev/guides/in-app-purchases/

Expo chính thức khuyến nghị 2 lựa chọn (SDK 57):

| Thư viện | Ưu | Nhược |
|---|---|---|
| **`react-native-purchases` (RevenueCat)** | Có backend quản lý entitlement, receipt validation, analytics sẵn; ít code | Phụ thuộc bên thứ ba, có phí theo doanh thu, **phải khai vào Data Safety** |
| **`expo-iap`** | Expo Module, không phụ thuộc bên thứ ba, theo chuẩn OpenIAP, dùng Play Billing 8.x | Phải tự làm verification ở backend |

**Khuyến nghị**: **`expo-iap`** — vì Nexora đã có backend entitlement riêng (`EntitlementSummaryResponse`, `/me/billing`), thêm RevenueCat sẽ trùng chức năng và thêm một bên thứ ba phải công bố trong Data Safety.

**Việc làm**
1. `npx expo install expo-iap`
2. Thêm config plugin theo doc của package.
3. Rebuild development build (IAP cần native code — **không chạy trên Expo Go**).

**Acceptance**: dev build mới cài được, `expo-iap` khởi tạo không lỗi.

---

## Bước 2.2 · Tạo product trên Google Play Console

**Việc làm**
1. Upload một bản AAB lên **Internal testing** trước (Play Console yêu cầu có bản build mới cho phép tạo IAP product).
2. Tạo product tương ứng **từng** `planPriceId` hiện có:
   - Nếu one-time → **In-app products**.
   - Nếu tự gia hạn → **Subscriptions** với base plan + offer.
3. Đặt `productId` theo quy ước rõ ràng: `nexora_pro_1m`, `nexora_pro_3m`, `nexora_pro_12m`.
4. Thêm license tester (email của team) vào Play Console → Setup → License testing để test không bị trừ tiền thật.

**Acceptance**: product hiện trạng "Active" trên Console; `getProducts()` từ app trả về đúng danh sách với giá địa phương hóa.

---

## Bước 2.3 · Backend: endpoint verify purchase token

**Việc làm (backend team)**
1. Tạo service account Google Cloud, bật **Google Play Developer API**, link với Play Console.
2. Thêm endpoint `POST /billing/google-play/verify`:
   - Input: `{ productId, purchaseToken, orderId? }`
   - Gọi `purchases.products.get` (one-time) hoặc `purchases.subscriptionsv2.get` (subscription) để verify token.
   - Kiểm: token hợp lệ, `packageName` == `com.nexora.app`, `productId` khớp, chưa được consume/acknowledge bởi user khác.
   - Cấp entitlement, ghi order, trả `EntitlementSummaryResponse`.
   - **Idempotent theo `purchaseToken`** — cùng token gọi 2 lần chỉ cấp entitlement 1 lần.
3. Thêm **Real-time Developer Notifications (RTDN)** qua Pub/Sub để nhận sự kiện gia hạn / hủy / hoàn tiền / chargeback → tự động thu hồi entitlement.
4. Giữ bảng mapping `productId` ↔ `planPriceId` để không phá contract hiện tại.

**Acceptance**
- [ ] Gửi purchase token thật (từ license tester) → entitlement được cấp.
- [ ] Gửi lại cùng token → không cấp thêm (idempotent).
- [ ] Gửi token giả/sửa đổi → bị từ chối 400/403.
- [ ] Hủy subscription trên Play → RTDN tới → entitlement bị thu hồi.

---

## Bước 2.4 · Client: viết lại luồng mua

**File**: `src/app/(app)/pricing/index.tsx`, `src/api/pricing.api.ts`

**Việc làm**

1. **Xóa hoàn toàn**:
   - Dòng 91: `Linking.openURL(data.checkout.url)`.
   - Import `Linking` nếu không còn dùng.
   - `pricingApi.createCheckoutSession` / `getCheckoutStatus` / `refreshCheckoutSession` — hoặc đánh dấu `@deprecated — web only, KHÔNG dùng trên native`.
   - Field `checkout` trong `CheckoutResponse` không được dùng ở native.

2. **Luồng mới**:
   ```
   1. Khởi tạo IAP connection khi vào màn pricing
   2. Lấy danh sách product từ store (getProducts / getSubscriptions)
   3. Hiển thị giá LẤY TỪ STORE (không phải từ backend) — Play yêu cầu
      "In-app pricing must match the pricing displayed in the user-facing Play billing interface"
   4. User bấm mua → requestPurchase(productId)
   5. Nhận purchase listener → lấy purchaseToken
   6. POST /billing/google-play/verify → backend cấp entitlement
   7. finishTransaction / acknowledgePurchase (BẮT BUỘC — không ack trong 3 ngày, Google tự hoàn tiền)
   8. invalidateQueries(['currentUser']) → UI cập nhật quota
   ```

3. **Xử lý các case bắt buộc**:
   | Case | Xử lý |
   |---|---|
   | User hủy giữa flow | Không báo lỗi đỏ, chỉ đóng modal |
   | Mua xong nhưng verify backend fail | **Không** finishTransaction; retry với backoff; lưu pending purchase để retry khi mở app lại |
   | App bị kill giữa flow | Khi khởi động, gọi `getAvailablePurchases()` để lấy purchase chưa ack → verify → ack |
   | User đã có entitlement | Disable nút mua gói đó (logic `isCurrentPlan` hiện đã có — giữ) |
   | Restore purchase | Thêm nút "Khôi phục giao dịch" → `getAvailablePurchases()` → verify lại |

4. **Thêm nút quản lý subscription** (nếu là subscription): mở
   `https://play.google.com/store/account/subscriptions?sku=<productId>&package=com.nexora.app`
   — đây là link Google cho phép và **bắt buộc phải có**.

**Cạm bẫy — quan trọng**
- **Không bao giờ tin client**: entitlement chỉ được cấp sau khi backend verify token với Google. Nếu tin `requestPurchase` thành công ở client, app bị hack quota trong 5 phút.
- **Phải acknowledge trong 3 ngày** — nếu không Google tự động hoàn tiền cho user.
- **Không được để cả link web checkout "để tham khảo"** trong app Android. Policy cấm cả "In-app webviews, buttons, links, messaging".

**Acceptance**
- [ ] `grep -rn "Linking.openURL" src/app/\(app\)/pricing/` → 0 kết quả.
- [ ] `grep -rn "checkout.url" src/` → 0 kết quả ở code chạy trên native.
- [ ] Mua thử bằng license tester → entitlement được cấp, quota tăng đúng.
- [ ] Kill app giữa lúc mua → mở lại → purchase được khôi phục và ack.
- [ ] Giá hiển thị trong app **khớp chính xác** giá Google hiển thị trong dialog thanh toán.
- [ ] Có nút quản lý/hủy subscription dẫn tới trang Google Play.
- [ ] Video quay toàn bộ flow lưu `evidence/P0-01-play-billing-flow.mp4`.

---

## Bước 2.5 · Cập nhật chính sách thanh toán trong app

**File**: `src/components/ui/legal-policy-modal.tsx` — tab `payment` (dòng ~431-470)

**Việc làm**
1. Dòng 438: viết lại cho khớp thực tế — nếu chỉ dùng Play Billing trên Android thì nói đúng thế, bỏ mọi tham chiếu "đối tác thanh toán chính thức".
2. Dòng 456-463 (**chính sách hoàn tiền**): viết lại theo quy trình Google Play. Không được công bố chính sách hoàn tiền riêng mâu thuẫn với Google. Nêu: hoàn tiền theo chính sách Google Play + link tới trang hỗ trợ của Google; email `support@nexora.vn` chỉ là kênh hỗ trợ bổ sung.
3. Dòng 466-469 (hủy gói): thêm hướng dẫn hủy qua **Google Play → Subscriptions**, kèm nút mở trực tiếp.

**Acceptance**: mọi câu trong tab "Thanh toán" đều kiểm chứng được bằng hành vi thật của app.

---

# PHASE 3 — Pháp lý & quyền dữ liệu (P0-04, P0-07, P0-08) · 5–7 người-ngày

## Bước 3.1 · Thêm cơ chế báo cáo nội dung AI (P0-04)

**Mục tiêu**: đáp ứng nguyên văn *"in-app user reporting or flagging features that allow users to report or flag offensive content to developers without needing to exit the app."*

**Việc làm**

1. **Backend**: `POST /content-reports`
   ```
   {
     "contentType": "interview_question" | "interview_report" | "coaching_note"
                  | "cv_analysis" | "learning_path" | "star_suggestion",
     "contentId": "<uuid>",
     "reasonCode": "offensive" | "inaccurate" | "irrelevant"
                 | "privacy_violation" | "discriminatory" | "other",
     "description": "<text, optional, max 1000>",
     "contentSnapshot": "<đoạn nội dung bị báo cáo, để moderation review>"
   }
   ```
   Trả `{ reportId, receivedAt }`.

2. **Client — component tái sử dụng**: tạo `src/components/moderation/ReportContentButton.tsx`
   - Props: `contentType`, `contentId`, `contentSnapshot`.
   - UI: icon cờ nhỏ, nhấn mở bottom sheet chọn lý do + ô mô tả + nút gửi.
   - Sau khi gửi: toast xác nhận "Cảm ơn bạn. Chúng tôi sẽ xem xét nội dung này."
   - Có thể gửi **khi chưa đăng nhập**? Không cần — nội dung AI luôn ở sau đăng nhập.

3. **Gắn vào TẤT CẢ surface có nội dung AI** (đây là phần dễ bị bỏ sót):

   | File | Vị trí gắn |
   |---|---|
   | `src/app/(app)/interview/[id].tsx` | Cạnh mỗi câu hỏi AI |
   | `src/components/interview/QuickCoachingModal.tsx` | Trong modal coaching |
   | `src/app/(app)/interview/report/[id].tsx` | Header báo cáo + từng mục nhận xét năng lực |
   | `src/app/(app)/cv-analysis/[id].tsx` | Header kết quả phân tích |
   | `src/components/cv-analysis/result-view/CVAnalysisResultTabs.tsx` | Mỗi tab kết quả |
   | `src/app/(app)/growth/learning-path.tsx` | Mỗi item gợi ý |
   | `src/app/(app)/growth/skill-profile.tsx` | Kết quả đánh giá skill |
   | `src/app/(app)/star-builder/index.tsx` | Gợi ý STAR do AI sinh |
   | `src/app/(app)/scenarios/[id].tsx` | Nội dung tình huống |

4. **Thêm nhãn "Nội dung do AI tạo"** ở các surface trên — vừa là kỳ vọng ngày càng chặt của Play với app AI, vừa giảm rủi ro Misrepresentation (user không nhầm đây là đánh giá của người thật).

5. **Quy trình nội bộ**: tài liệu hóa cách team xử lý report (SLA phản hồi, ai review, làm gì với nội dung bị báo cáo) → lưu `docs/moderation-process.md`. Policy yêu cầu *"utilize user reports to inform content filtering and moderation"* — cần có bằng chứng quy trình, không chỉ có nút.

6. **Cập nhật chính sách**: thêm section vào tab "Điều khoản" của `legal-policy-modal.tsx` nói rõ có cơ chế báo cáo và cách team xử lý.

**Cạm bẫy**
- Đừng chỉ đặt 1 nút ở màn Cài đặt — policy nói "report or flag offensive content", tức là phải gắn với **nội dung cụ thể**, ngay tại nơi nội dung xuất hiện.
- `ProductFeedbackModal` **không** thay thế được — đó là review sản phẩm, còn có `allowPublicDisplay` để đăng công khai.

**Acceptance**
- [ ] Đi qua 9 surface ở bảng trên, mỗi surface bấm được nút báo cáo và gửi thành công.
- [ ] Backend nhận và lưu đúng `contentType` + `contentId`.
- [ ] Có nhãn "Nội dung do AI tạo" hiển thị tại các surface AI.
- [ ] `docs/moderation-process.md` tồn tại và mô tả quy trình thật.
- [ ] Screenshot 9 surface lưu `evidence/P0-04-ai-report-*.png`.

---

## Bước 3.2 · Hoàn thiện luồng xóa tài khoản (P0-07)

**Việc làm**

### 3.2a — Trang web xóa tài khoản (bắt buộc bởi Play)

Dựng trang tại `https://nexora.vn/xoa-tai-khoan` (và alias `/account-deletion`), nội dung **phải có**:
1. Tên app rõ ràng: **"Nexora AI"** và tên developer.
2. Form: email tài khoản + lý do (tùy chọn) + nút gửi yêu cầu.
3. Bảng liệt kê **dữ liệu nào bị xóa**: tài khoản, CV đã upload, JD, lịch sử phỏng vấn, báo cáo, career goals, feedback.
4. Bảng liệt kê **dữ liệu nào được giữ lại, bao lâu, vì sao**: ví dụ hóa đơn/giao dịch giữ theo quy định kế toán, log chống gian lận giữ N ngày.
5. Thời gian xử lý (ví dụ: vô hiệu hóa ngay, hard-delete trong tối đa 30 ngày).
6. Kênh liên hệ nếu có vấn đề.

Trang này phải **truy cập được không cần đăng nhập** và không bị chặn bởi robots/paywall.

### 3.2b — Backend: xóa thật, không chỉ "ghi nhận yêu cầu"

1. `POST /me/deletion-requests` → soft-delete/vô hiệu hóa **ngay**, trả `{ requestId, status, scheduledHardDeleteAt }`.
2. Job xóa cứng theo SLA (xóa DB record, xóa object trên S3, xóa embedding/vector nếu có, xóa log chứa PII).
3. `GET /me/deletion-requests/current` → cho app hiển thị trạng thái.
4. `DELETE /me/deletion-requests/current` → huỷ yêu cầu trong grace period.

### 3.2c — Client

File: `src/app/(app)/account/index.tsx:171-198`

1. Dialog xác nhận **2 bước**: bước 1 liệt kê dữ liệu sẽ bị xóa, bước 2 yêu cầu gõ email hoặc từ `XÓA` để xác nhận. Dialog hiện tại chỉ 1 bước.
2. Sau khi API thành công, hiển thị `scheduledHardDeleteAt` cho user biết mốc thời gian.
3. Nếu user đăng nhập lại trong grace period → hiện banner "Tài khoản đang chờ xóa vào [ngày]" + nút "Huỷ yêu cầu xóa".
4. Cập nhật tab "Xóa dữ liệu" trong `legal-policy-modal.tsx` (dòng 471-500): thêm danh sách dữ liệu giữ lại + link tới trang web xóa.

**Acceptance**
- [ ] Trang web xóa tài khoản sống, mở được ở chế độ ẩn danh, có nêu tên "Nexora AI".
- [ ] Yêu cầu xóa trong app → user bị logout, không đăng nhập lại được (hoặc vào được và thấy banner chờ xóa).
- [ ] Verify trong DB: record đã soft-delete với `scheduledHardDeleteAt`.
- [ ] Huỷ yêu cầu hoạt động trong grace period.
- [ ] Chính sách trong app liệt kê chính xác dữ liệu giữ lại.
- [ ] URL đã được nhập vào Play Console → Data safety → Data deletion.

---

## Bước 3.3 · Viết lại toàn bộ nội dung pháp lý cho khớp thực tế (P0-08)

> ⚠️ Làm bước này **sau cùng** trong Phase 3, khi đã biết chính xác app làm gì sau Phase 1–2.

**File**: `src/components/ui/legal-policy-modal.tsx`

**Bảng việc phải sửa**

| Dòng | Nội dung hiện tại | Sửa thành |
|---|---|---|
| `:507` (docMeta) | "Cập nhật lần cuối: 25.09.2026" | Ngày thật, định dạng rõ ràng (ví dụ "Cập nhật lần cuối: 15 tháng 10, 2026") |
| `:336` | "Giọng nói & Âm thanh: Dữ liệu ghi âm giọng nói…" | Nếu Đường B → **xóa**. Nếu Đường A → nêu rõ audio được gửi tới **Microsoft Azure Speech Services** để chuyển văn bản, lưu bao lâu, có dùng train model không |
| `:340` | "Nexora không bao giờ chia sẻ hoặc bán dữ liệu cá nhân cho bên thứ ba vì mục đích quảng cáo" | Thêm section mới liệt kê **đầy đủ** bên thứ ba nhận dữ liệu: Azure Speech (nếu có), nhà cung cấp LLM (xác nhận với backend: OpenAI? Azure OpenAI? Gemini?), hosting (Render), crash reporting (Sentry sau Phase 4), storage (S3/R2) |
| `:344` | "trích xuất bản sao dữ liệu cá nhân (định dạng JSON)" | Giữ — nhưng chỉ sau khi Bước 1.5 xong |
| `:349-355` | Section 4 "Quyền truy cập thiết bị & ghi âm" | Nếu Đường B → xóa. Nếu Đường A → nêu đúng quyền nào, dùng khi nào, có ghi nền hay không |
| `:359` | "từ 18 tuổi trở lên (hoặc từ 13 tuổi với sự giám sát…)" | **Chỉ 18+**, bỏ mệnh đề 13+ (xem P2-13) |
| `:438` | "…hoặc cơ chế thanh toán trong ứng dụng (Google Play Billing)" | Viết lại khớp Phase 2 |
| `:456-463` | Chính sách hoàn tiền riêng 7 ngày | Viết lại theo Google Play refund policy |
| `:486-490` | Hướng dẫn xóa tài khoản | Thêm link trang web xóa |
| `:253`, `:507` | Link `https://nexora.vn/privacy` | Xác minh URL sống; thêm link riêng cho Terms và Data Deletion |

**Việc làm thêm**
1. Dựng 3 trang web thật: `/privacy`, `/terms`, `/xoa-tai-khoan`. Nội dung phải **đồng bộ** với nội dung trong app.
2. Thêm section "Quyền của bạn" (GDPR-style): truy cập, sửa, xóa, xuất, phản đối xử lý.
3. Thêm section về nội dung AI: nội dung do AI tạo có thể không chính xác, không phải tư vấn nghề nghiệp chuyên nghiệp, có cơ chế báo cáo.
4. Có người rà soát pháp lý (không phải dev) đọc lại toàn bộ.

**Acceptance**
- [ ] Mỗi câu khẳng định trong modal pháp lý đối chiếu được với một hành vi thật của app — lập bảng đối chiếu trong `evidence/legal-claims-verification.md`.
- [ ] 3 URL web trả HTTP 200 khi test từ mạng ngoài, mở được ở chế độ ẩn danh.
- [ ] Nội dung trong app và trên web khớp nhau.
- [ ] Đã liệt kê **toàn bộ** bên thứ ba nhận dữ liệu.

---

# PHASE 4 — Sửa lỗ hổng bảo mật P1 · 8–11 người-ngày

## Bước 4.1 · Chặn rò rỉ log ở production (P1-01)

**Việc làm**

1. **`src/services/logger.ts:27-35`** — bọc `console.error` trong `__DEV__`:
   - Ở dev: log đầy đủ như hiện tại.
   - Ở production: **không** `console.error`; gửi tới crash reporter (Bước 4.11) **sau khi scrub**.

2. **Viết hàm scrub** trước khi log/gửi bất kỳ error nào:
   - Xóa `config.headers.Authorization`, `config.headers.Cookie`, `config.headers['Idempotency-Key']`.
   - Xóa `config.data` và `response.data` nếu có thể chứa PII (CV content, câu trả lời, email, mật khẩu).
   - Giữ lại: `status`, `code`, `requestId`, `method`, **pathname đã loại query string**.

3. **Xóa hoặc guard 6 chỗ `console.*` trực tiếp**:
   | File:line | Xử lý |
   |---|---|
   | `src/services/storage.ts:26, 47, 67` | Bọc `__DEV__`; ở prod chỉ gửi mã lỗi, **không** log tên key |
   | `src/app/(app)/resumes/index.tsx:73` | Đổi `console.error(error)` → `logger.error('Resume upload failed', error)` |
   | `src/app/(app)/resumes/index.tsx` (handleUpload catch) | Tương tự |
   | `src/app/(app)/career-goals/create.tsx` | Tương tự |
   | `src/services/speech.ts`, `src/services/tts.ts` | Nếu Đường B đã xóa file thì bỏ qua |

4. **Thêm ESLint rule** `no-console` (cho phép trong `logger.ts` qua override) để chặn tái phát.

**Acceptance**
- [ ] `grep -rn "console\." src/ --include=*.ts --include=*.tsx | grep -v __DEV__ | grep -v logger.ts` → 0 kết quả.
- [ ] Build production, chạy trên máy thật, kích lỗi API 500 → `adb logcat | grep -i "Bearer\|Authorization\|@gmail"` → **0 kết quả**.
- [ ] ESLint fail nếu thêm `console.log` mới.
- [ ] Log logcat lưu `evidence/P1-01-no-token-in-logcat.txt`.

---

## Bước 4.2 · Xóa lệnh log tên key SecureStore (P1-01 phần 2)

**File**: `src/services/storage.ts:26, 47, 67`

```
console.error(`Error reading ${key} from SecureStore:`, error);
```

Dòng này in tên key (`nexora_access_token`) ra logcat. Không leak giá trị nhưng tiết lộ cấu trúc lưu trữ cho attacker.

**Việc làm**: bỏ `key` khỏi message, chỉ log mã lỗi chung: `logger.error('SecureStore read failed')`.

**Acceptance**: `grep -n "nexora_access_token\|nexora_refresh_token" src/services/storage.ts` → chỉ còn ở khai báo const, không có trong log.

---

## Bước 4.3 · Thống nhất chiến lược refresh token (P1-03 + P1-14)

**Vấn đề**: refresh token được lưu SecureStore nhưng không dùng; refresh thực tế dựa vào cookie — trên Android cookie không đảm bảo persist qua restart → user bị logout bất ngờ.

**Việc làm — chọn MỘT chiến lược và làm nhất quán**

### Chiến lược khuyến nghị: token-in-body cho native, cookie cho web

1. **Native**:
   - `POST /auth/refresh` gửi `{ refreshToken }` lấy từ SecureStore trong body.
   - Đặt `withCredentials: false` cho native (không cần cookie).
   - Backend trả access token mới **và** refresh token mới (rotation) → client ghi lại cả hai.
   - Nếu refresh token bị từ chối (revoked/hết hạn) → `clearTokens()` + logout, như hiện tại.
2. **Web**: giữ cookie `HttpOnly; Secure; SameSite=Strict` do backend set; **không** ghi refresh token vào `localStorage`.
3. **Refresh token rotation** + phát hiện reuse: nếu một refresh token đã dùng bị dùng lại → revoke cả family (dấu hiệu token bị đánh cắp).
4. Sửa `src/context/auth-context.tsx:28-51` (`hydrateSessionAsync`) cho khớp: đọc refresh token từ SecureStore, gửi trong body.
5. Sửa `src/api/client.ts:152-162` tương tự.
6. **Backend cần làm**: cookie `SameSite=Strict` + `HttpOnly` + `Secure`; CORS allowlist cụ thể (không `*` khi `withCredentials`); kiểm origin cho `/auth/refresh`.

**Acceptance**
- [ ] Login trên máy thật → force stop app → mở lại sau 1 giờ → **vẫn đăng nhập**, không bị kick.
- [ ] Bật airplane mode → mở app → hiện lỗi mạng, **không** tự logout.
- [ ] `grep -rn "getRefreshToken" src/` → có ít nhất 1 chỗ dùng thật.
- [ ] Refresh token cũ (đã rotate) dùng lại → bị từ chối 401.
- [ ] Test session persistence 7 ngày liên tục lưu `evidence/P1-03-session-persistence.md`.

---

## Bước 4.4 · Thêm auth guard cho route group `(app)` (P1-04)

**File**: `src/app/(app)/_layout.tsx`

**Việc làm**

1. Thêm guard theo đúng pattern đã dùng ở `src/app/(tabs)/_layout.tsx`:
   - Lấy `{ isAuthenticated, isLoading }` từ `useAuth()`.
   - `isLoading` → render splash/spinner.
   - `!isAuthenticated` → `<Redirect href="/(auth)/login" />`.
   - Ngược lại → render `<Stack>` như hiện tại.
2. Cân nhắc tách thành component `AuthGuard` dùng chung cho cả `(app)` và `(tabs)` để không lặp logic.
3. Xem lại `src/app/(auth)/*` — cần guard **ngược**: nếu đã đăng nhập thì redirect về `/(tabs)/home` (tránh user đã login mở được màn login qua deep link).

**Acceptance**
- [ ] Khi chưa đăng nhập, chạy từng deep link sau đều bị redirect về login:
  ```
  adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/account" com.nexora.app
  adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/pricing" com.nexora.app
  adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/resumes" com.nexora.app
  adb shell am start -a android.intent.action.VIEW -d "nexoramobile://(app)/interview/abc" com.nexora.app
  ```
- [ ] Không có toast lỗi 401 nào bắn ra trong các case trên.
- [ ] Output lệnh lưu `evidence/P1-04-deeplink-guard.txt`.

---

## Bước 4.5 · Chặn lưu token vào `localStorage` ở web (P1-05)

**File**: `src/services/storage.ts:11-70`

**Việc làm**
1. Tách thành hai lớp lưu trữ rõ ràng:
   - `secureTokenStorage` — native: SecureStore. Web: **chỉ in-memory**, không `localStorage`.
   - `preferenceStorage` — cho dữ liệu không nhạy cảm (`nexora_hide_latest_analysis_popup`): `localStorage` trên web là chấp nhận được.
2. Web: access token giữ trong memory; khi reload trang thì gọi `/auth/refresh` (cookie) để lấy lại — đây là pattern chuẩn.
3. Không bao giờ ghi refresh token vào `localStorage` ở web.

**Acceptance**
- [ ] Trên web build, mở DevTools → Application → Local Storage → **không** có `nexora_access_token` hay `nexora_refresh_token`.
- [ ] Reload trang web → vẫn đăng nhập (qua cookie refresh).

---

## Bước 4.6 · Tăng cường bảo mật tầng mạng (P1-06)

**Việc làm**
1. Trong `expo-build-properties` (cấu hình ở Bước 5.1):
   - `usesCleartextTraffic: false`
   - `networkInspector: false` cho profile production (mặc định là `true`).
2. Cân nhắc **certificate pinning**:
   - Đánh giá rủi ro: app xử lý CV (PII cao) → nên có.
   - Nhưng pinning cứng sẽ **brick app** khi backend đổi cert. Bắt buộc phải: pin **2 key** (cert hiện tại + backup), có cơ chế kill-switch, và theo dõi ngày hết hạn cert.
   - Nếu team chưa đủ quy trình vận hành cert → **đừng pin**, thay bằng `networkSecurityConfig` giới hạn trust anchor về system CA (loại user-installed CA) — rẻ hơn và đủ chặn MITM cơ bản.
3. Kiểm `API_BASE_URL` luôn là `https://` — thêm assertion khi khởi động app.

**Acceptance**
- [ ] `android/app/src/main/AndroidManifest.xml` sau prebuild có `android:usesCleartextTraffic="false"`.
- [ ] Chạy app qua mitmproxy với CA tự cài → request **thất bại** (nếu đã loại user CA).
- [ ] App không gọi được bất kỳ URL `http://` nào.

---

## Bước 4.7 · Chặn rò rỉ thông điệp lỗi backend ra UI (P1-07)

**File**: `src/utils/errorTranslator.ts:26`, `src/api/client.ts:117-119`

**Việc làm**
1. Đổi fallback ở dòng 26 từ `return englishMessage` thành **message chung**:
   `'Đã có lỗi xảy ra. Vui lòng thử lại hoặc liên hệ hỗ trợ.'`
2. Mở rộng bảng map theo **mã lỗi** (`error.code` từ envelope backend) thay vì so khớp chuỗi tiếng Anh — bền vững hơn nhiều khi backend đổi wording.
3. Hiển thị `requestId` (ngắn, 8 ký tự đầu) trong toast lỗi để support tra được, nhưng **không** hiện message thô.
4. Với lỗi validation form (4xx có `errors`), vẫn map từng field — logic hiện tại (dòng 42-48) đúng, giữ lại.
5. Log message gốc qua `logger` (đã scrub) để dev debug được.

**Acceptance**
- [ ] Backend trả message chứa "SqlException: table Users column Email…" → toast **chỉ** hiện message chung tiếng Việt.
- [ ] Toast có `requestId` để tra cứu.
- [ ] Test với 5 loại lỗi backend khác nhau, không có lỗi nào lộ chi tiết nội bộ.

---

## Bước 4.8 · Siết validate upload CV & dọn cache PII (P1-08)

**File**: `src/app/(app)/resumes/index.tsx:45-63, 128-140`

**Việc làm**

1. **Validate trước khi gọi presign**:
   - Nếu `!file.size` → từ chối, yêu cầu chọn lại (không gửi `size: 0`).
   - Giới hạn cứng: `MAX_RESUME_BYTES = 10 * 1024 * 1024` (10MB) — điều chỉnh theo giới hạn backend.
   - Nếu `!file.mimeType` → suy ra từ extension, **không** hardcode `'application/pdf'`. Nếu không suy được → từ chối.
   - Allowlist mimeType: chỉ `application/pdf` và `application/vnd.openxmlformats-officedocument.wordprocessingml.document`.
   - Thông báo lỗi rõ ràng tiếng Việt cho từng trường hợp.

2. **Dọn file cache sau upload** (cả khi thành công và khi thất bại):
   - Trong `finally`/`onSettled`: `await FileSystem.deleteAsync(file.uri, { idempotent: true })`.
   - Thêm hàm dọn cache khi app khởi động: quét `cacheDirectory` xóa file tài liệu cũ hơn N giờ.

3. **Sửa kiểm tra status của S3 PUT** (dòng 60): `if (uploadResult.status < 200 || uploadResult.status >= 300)` — S3 có thể trả **204**, code hiện tại chỉ chấp nhận đúng 200.

4. Thêm progress indicator + khả năng huỷ upload (dùng `FileSystem.createUploadTask` nếu cần progress).

5. **Backend phải validate lại độc lập** — client-side validation không phải kiểm soát bảo mật. Xác nhận backend: kiểm magic bytes, giới hạn size ở presign policy, scan malware nếu có.

**Acceptance**
- [ ] Chọn file 50MB → bị từ chối ngay, không upload.
- [ ] Chọn file `.txt` đổi tên thành `.pdf` → backend từ chối (verify với backend team).
- [ ] Sau upload, `FileSystem.getInfoAsync(file.uri)` → `exists: false`.
- [ ] Liệt kê `cacheDirectory` sau 3 lần upload → không còn file CV nào.
- [ ] Bằng chứng lưu `evidence/P1-08-cache-cleanup.txt`.

---

## Bước 4.9 · Vô hiệu Azure Speech token khi đổi phiên (P1-09)

> Nếu chọn Đường B ở Phase 1 (đã xóa `speechTokenManager.ts`) → **bước này tự động đóng**, chỉ cần xác nhận file đã bị xóa.

**File**: `src/context/auth-context.tsx:112-126`, `src/services/speechTokenManager.ts`

**Việc làm**
1. Trong `logoutAsync()`: gọi `clearInterviewSpeechAuthorizationCache()` (không tham số → xóa toàn bộ).
2. Trong listener `onAuthError` (`auth-context.tsx:140-145`): gọi tương tự.
3. Trong `loginAsync()` (trước `queryClient.clear()`): gọi tương tự, phòng trường hợp đổi tài khoản.
4. Cân nhắc: khi một phiên phỏng vấn kết thúc → `clearInterviewSpeechAuthorizationCache(interviewId)` để không giữ token quá thời gian cần.

**Acceptance**
- [ ] `grep -rn "clearInterviewSpeechAuthorizationCache" src/` → xuất hiện ở `auth-context.tsx` (≥3 chỗ), không chỉ ở định nghĩa.
- [ ] Test: login A → vào interview (lấy token) → logout → login B → token cache rỗng (kiểm qua log dev).

---

## Bước 4.10 · Dùng CSPRNG cho idempotency key (P1-10)

**File**: `src/api/client.ts:21-30`

**Việc làm**
1. `npx expo install expo-crypto`
2. Viết lại `createIdempotencyKey()`:
   - Ưu tiên `Crypto.randomUUID()` của `expo-crypto`.
   - Nếu không có → `Crypto.getRandomBytes(16)` rồi format thành UUIDv4.
   - **Xóa hoàn toàn** nhánh `Math.random()`.
3. Thống nhất: bỏ `react-native-uuid` trong `src/api/pricing.api.ts:12`, dùng chung `createIdempotencyKey()` cho toàn bộ codebase (hiện có 2 cơ chế song song).
4. Nếu sau khi thống nhất `react-native-uuid` không còn dùng ở đâu → gỡ khỏi `package.json`.

**Acceptance**
- [ ] `grep -rn "Math.random" src/api/` → 0 kết quả.
- [ ] `grep -rn "react-native-uuid" src/` → 0 kết quả (hoặc chỉ 1 nơi duy nhất nếu quyết định giữ).
- [ ] Sinh 10.000 key → không có trùng lặp, phân bố đều.

---

## Bước 4.11 · Tích hợp crash reporting + ErrorBoundary (P1-11)

**Việc làm**

1. **Chọn giải pháp**: Sentry (`@sentry/react-native`) — tích hợp tốt với Expo, có config plugin, có source map upload cho Hermes.
   > ⚠️ Sentry là bên thứ ba nhận dữ liệu → **phải khai vào Data Safety** và **phải nêu trong chính sách quyền riêng tư**.

2. **Cấu hình bắt buộc để không biến crash reporting thành lỗ rò PII**:
   - `sendDefaultPii: false`
   - `beforeSend` hook: scrub header `Authorization`, `Cookie`; scrub body request/response; scrub email; scrub nội dung CV/câu trả lời.
   - Không gắn `setUser({ email })` — chỉ dùng user ID đã hash.
   - `tracesSampleRate` thấp ở production (0.1) để không gửi quá nhiều dữ liệu.

3. **Nối vào 2 điểm đã có sẵn placeholder**:
   - `src/services/logger.ts:31-34` → `Sentry.captureException(scrubbed)`.
   - `src/services/analytics.ts:22-26` (`recordError`) → tương tự cho production.

4. **Thêm ErrorBoundary gốc** trong `src/app/_layout.tsx`:
   - Bọc `<AppProvider>` hoặc dùng `Sentry.wrap()`.
   - Fallback UI tiếng Việt, có nút "Thử lại" và "Báo cáo sự cố".
   - Hiện tại app **không có** ErrorBoundary nào → JS error không bắt được sẽ gây màn hình trắng.

5. **Upload source map** cho build production để stack trace đọc được (cấu hình trong EAS build hook).

6. **Consent**: nếu quyết định cần consent cho telemetry, thêm màn hình onboarding hỏi ý kiến, và tôn trọng lựa chọn.

**Acceptance**
- [ ] Gây crash cố ý trên build production → crash xuất hiện trên Sentry dashboard trong 1 phút.
- [ ] Kiểm event trên Sentry: **không** có email, **không** có header `Authorization`, **không** có nội dung CV.
- [ ] Stack trace đã de-minify (đọc được tên file/dòng).
- [ ] Gây JS error trong một component → ErrorBoundary hiện fallback UI, app không trắng màn hình.
- [ ] Screenshot event Sentry (đã che dữ liệu) lưu `evidence/P1-11-sentry-scrubbed.png`.

---

## Bước 4.12 · Giới hạn và làm sạch analytics (P1-13)

**File**: `src/services/analytics.ts`

**Việc làm**
1. Giới hạn buffer: giữ tối đa 100 event, drop event cũ nhất (theo mẫu bounded set đã dùng đúng ở `src/services/signalr.ts:23-26`).
2. Định nghĩa **schema chặt** cho event thay vì `Record<string, any>`: union type các event name được phép + params có kiểu rõ ràng. Mục đích: **chặn dev vô tình log PII**.
3. Thêm allowlist param key; strip mọi key không có trong allowlist trước khi lưu/gửi.
4. Thêm cơ chế consent: nếu user chưa đồng ý telemetry → `logEvent` no-op.
5. Nếu analytics chưa được dùng thật (chỉ log console ở dev) → cân nhắc **xóa service này** khỏi v1, thêm lại khi có SDK thật + consent flow. Đơn giản hơn và ít rủi ro hơn.

**Acceptance**
- [ ] Log 1000 event → `events.length <= 100`.
- [ ] Thử log event có key `email` → bị strip.
- [ ] TypeScript báo lỗi nếu log event name không có trong union.

---

# PHASE 5 — Hardening build, P2 & kiểm thử trước submit · 5–7 người-ngày

## Bước 5.1 · Cấu hình `expo-build-properties` (P1-02, P2-04)

**Doc**: https://docs.expo.dev/versions/v57.0.0/sdk/build-properties/

**Việc làm**

1. `npx expo install expo-build-properties` (nếu chưa làm ở Bước 0.3).
2. Thêm plugin vào `app.json` với các option **đúng tên của SDK 57**:
   ```
   android:
     compileSdkVersion: 36
     targetSdkVersion: 36
     minSdkVersion: <theo default SDK 57, kiểm tra trước>
     enableMinifyInReleaseBuilds: true        ← KHÔNG phải enableProguardInReleaseBuilds
     enableShrinkResourcesInReleaseBuilds: true
     usesCleartextTraffic: false
     networkInspector: false                  ← tắt ở production
     extraProguardRules: "<rule giữ class cần thiết>"
   ios:
     deploymentTarget: "<theo yêu cầu SDK 57>"
     networkInspector: false
   ```
3. **`extraProguardRules`** — R8 có thể strip class mà reflection cần. Cần giữ ít nhất:
   - Class của `expo-iap` / billing.
   - Model class được deserialize bằng reflection (nếu có).
   - Class của Reanimated / Worklets (thường lib tự cung cấp consumer rules, nhưng phải test).
4. **Bắt buộc test kỹ sau khi bật minify**: R8 là nguyên nhân số 1 của lỗi "chỉ xảy ra ở build release". Chạy **toàn bộ** happy path trên build release, không chỉ debug.

**Acceptance**
- [ ] Build release chạy được, **mọi** flow chính hoạt động (login, upload CV, phỏng vấn, báo cáo, thanh toán).
- [ ] `aapt2 dump badging <aab>` → `targetSdkVersion='36'`.
- [ ] Manifest có `usesCleartextTraffic="false"`.
- [ ] Decompile AAB → tên class/method đã bị obfuscate.
- [ ] Output lưu `evidence/P2-04-target-sdk.txt`.

---

## Bước 5.2 · Verify tuân thủ 16 KB page size (P2-04)

**Việc làm**
1. Build AAB production.
2. Giải nén AAB, lấy toàn bộ `.so` trong `base/lib/arm64-v8a/`.
3. Chạy script `check_elf_alignment.sh` (Android NDK) trên từng file.
4. Test thực tế trên **emulator Android 15 với 16 KB system image** — chạy full happy path.
5. Nếu có lib fail:
   - `lottie-react-native` **đang không được dùng** → gỡ luôn (xem Bước 5.3).
   - Lib khác → nâng version hoặc thay thế.

**Acceptance**
- [ ] Mọi `.so` báo `ALIGNED`.
- [ ] App chạy ổn định trên emulator 16 KB Android 15.
- [ ] Output script lưu `evidence/P2-04-16kb-alignment.txt`.

---

## Bước 5.3 · Dọn dependency & file rác (P2-02, P2-03, P2-12)

**Việc làm**

1. **Gỡ dependency không dùng** (đã verify bằng grep, 0 file sử dụng):
   ```
   npm uninstall lottie-react-native @lottiefiles/dotlottie-react expo-linking expo-constants
   ```
   ⚠️ Trước khi gỡ `expo-linking`: kiểm tra `expo-router` có cần nó làm peer dependency không. Nếu có thì giữ.
   ⚠️ `@lottiefiles/dotlottie-react` là package **web-only** trong app RN — ưu tiên gỡ đầu tiên.

2. **Xóa file rác khỏi git**:
   ```
   git rm --cached react_doctor.json react_doctor_utf8.json
   ```
   Thêm vào `.gitignore`: `react_doctor*.json`
   ⚠️ **Đọc nội dung 2 file này trước khi xóa** — kiểm xem có đường dẫn máy local / thông tin môi trường dev bị lộ trong lịch sử git không. Nếu có thông tin nhạy cảm thì cần cân nhắc rewrite history.

3. **Nén asset**: `assets/images/icon.png` (799 KB) và `logo-glow.png` (331 KB) → nén xuống dưới ~100 KB bằng pngquant/oxipng, giữ chất lượng đủ dùng.

4. **Kiểm lại `ios.icon`** (`app.json:13` trỏ `./assets/expo.icon` — là thư mục): xác nhận đúng ý định hoặc sửa thành file ảnh.

**Acceptance**
- [ ] `npx tsc --noEmit` pass sau khi gỡ dependency.
- [ ] `npx expo-doctor` không báo lỗi dependency.
- [ ] Kích thước AAB giảm so với trước.
- [ ] `git ls-files | grep react_doctor` → 0 kết quả.

---

## Bước 5.4 · Xử lý CVE trong dependency (P1-12)

**Việc làm**
1. **KHÔNG chạy `npm audit fix --force`** — nó sẽ hạ `expo-router` xuống v5 (breaking change nghiêm trọng).
2. Đánh giá lại từng CVE:
   - `uuid` <11.1.1 → chỉ trong toolchain build (`@expo/cli` → `xcode`), **không vào bundle runtime** → rủi ro thấp, ghi nhận và chấp nhận.
   - `decode-uri-component` ≤0.4.2 → **có** vào runtime qua `expo-router` → cần xử lý.
3. Thử `overrides` trong `package.json` cho `decode-uri-component` lên version đã patch, rồi **test đầy đủ routing** (deep link, query param, dynamic route). Nếu vỡ → revert và theo dõi Expo patch.
4. Tài liệu hóa quyết định accept-risk cho từng CVE còn lại vào `docs/release-audit/evidence/cve-risk-acceptance.md`.
5. Thiết lập `npm audit` trong CI với ngưỡng `--audit-level=high` để không chặn build vì moderate nhưng cảnh báo khi có high/critical mới.

**Acceptance**
- [ ] `npm audit --omit=dev --audit-level=high` → 0 vulnerability.
- [ ] Moderate còn lại có tài liệu accept-risk có người phê duyệt.
- [ ] CI có bước audit.

---

## Bước 5.5 · Cải thiện polling / hoàn thiện realtime (P2-01)

**Việc làm — chọn 1**

**A. Hoàn thiện SignalR** (đúng hướng dài hạn)
1. `npm install @microsoft/signalr`
2. Trong `src/services/signalr.ts`, thêm `HubConnectionBuilder` kết nối tới `getHubUrl()`, `accessTokenFactory` lấy từ `tokenStorage`.
3. Lắng nghe `resourceChanged`, dùng `isNewEvent()` (đã có, đúng) để dedupe, rồi `queryClient.invalidateQueries`.
4. Tắt/giảm mạnh `refetchInterval` khi SignalR đang kết nối.
5. Fallback về polling khi SignalR mất kết nối.

**B. Cải thiện polling** (nhanh hơn)
1. Thay `return 3000` cố định (`interview/report/[id].tsx:95`) bằng exponential backoff: 3s → 5s → 10s → 30s, cap 30s.
2. Dừng poll khi app vào background (`AppState` listener).
3. Đặt số lần poll tối đa; sau đó hiện nút "Tải lại" thủ công.

**Acceptance**
- [ ] Đo số request trong 5 phút chờ báo cáo: giảm ≥60% so với trước.
- [ ] App vào background → không còn request nào.
- [ ] Báo cáo vẫn cập nhật đúng khi xử lý xong.

---

## Bước 5.6 · Khai báo `blockedPermissions` và audit manifest (P2-08)

**Việc làm**
1. Thêm vào `app.json` → `android.blockedPermissions` những quyền không bao giờ cần:
   `CAMERA`, `READ_CONTACTS`, `ACCESS_FINE_LOCATION`, `ACCESS_COARSE_LOCATION`, `READ_EXTERNAL_STORAGE`, `WRITE_EXTERNAL_STORAGE`, `READ_PHONE_STATE`, `QUERY_ALL_PACKAGES`, `SYSTEM_ALERT_WINDOW`, `RECEIVE_BOOT_COMPLETED`…
   (điều chỉnh: nếu Bước 1.6 chọn Lựa chọn A thì `READ_MEDIA_IMAGES` phải được phép)
2. Chạy `npx expo prebuild --clean --platform android`.
3. Mở `android/app/src/main/AndroidManifest.xml`, **liệt kê toàn bộ** `uses-permission` và đối chiếu: mỗi quyền phải giải thích được vì sao cần.
4. Lưu danh sách vào `evidence/final-permissions.md` — đây cũng là tài liệu cần khi trả lời reviewer.
5. Thêm bước kiểm tra này vào CI/checklist release để không bị quyền lạ lọt vào bản sau.

**Acceptance**
- [ ] Danh sách quyền cuối cùng ≤ 3 quyền, mỗi quyền có lý do rõ ràng.
- [ ] **Không** có `RECORD_AUDIO` nếu chọn Đường B.
- [ ] Không có quyền nào team không nhận ra.

---

## Bước 5.7 · Dựng hạ tầng test (P2-07)

**Việc làm**
1. Cài `jest-expo` + `@testing-library/react-native`, thêm script `"test": "jest"` và `"test:ci": "jest --ci --coverage"`.
2. **Viết lại `tests/authParity.test.mjs`** — test hiện tại là structural (regex trên source), vỡ khi refactor mà không phát hiện lỗi thật. Chuyển thành test hành vi:
   - `validatePassword('abc')` → trả message độ dài.
   - `validatePassword('Abcdef1!')` → trả `null`.
   - Render `<LoginScreen />`, nhập mật khẩu yếu, submit → thấy message lỗi trên UI.
3. Bổ sung unit test cho các module security-critical:
   | Module | Test cần có |
   |---|---|
   | `src/utils/errorTranslator.ts` | Không leak message thô; map đúng từng mã lỗi |
   | `src/services/speechTokenManager.ts` (nếu giữ) | Từ chối token hết hạn/malformed; single-flight đúng; clear cache hoạt động |
   | `src/api/client.ts` | Interceptor 401 single-flight; queue resolve đúng; không refresh cho `/auth/login` |
   | `createIdempotencyKey` | Không trùng, không dùng `Math.random` |
   | `src/services/signalr.ts` | `isNewEvent` dedupe + bounded set |
   | `src/utils/billing-presentation.ts` | Format quota/giá đúng |
4. Thêm CI (GitHub Actions): `tsc --noEmit` + `expo lint` + `jest` + `npm audit --audit-level=high` trên mọi PR.

**Acceptance**
- [ ] `npm test` chạy được và pass.
- [ ] Coverage ≥ 60% cho `src/utils/` và `src/services/`.
- [ ] CI chạy trên PR và chặn merge khi fail.

---

## Bước 5.8 · Sửa cấu hình version (P2-05)

**Việc làm**
1. `eas.json` có `"appVersionSource": "remote"` → xóa `android.versionCode` và `ios.buildNumber` khỏi `app.json` để tránh nhầm lẫn (EAS quản lý).
2. Hoặc đổi sang `"appVersionSource": "local"` và quản lý thủ công — nhưng `remote` + `autoIncrement: true` (đã có ở profile `production`) là lựa chọn tốt hơn.
3. Sửa `ios.buildNumber: "1.0.0"` — buildNumber phải là số nguyên tăng dần, không phải semver.
4. Thiết lập quy ước versioning: `expo.version` là semver hiển thị cho user; versionCode do EAS tăng tự động.

**Acceptance**: build production 2 lần liên tiếp → versionCode tăng đúng, không conflict khi upload lên Play Console.

---

## Bước 5.9 · Rà soát cuối trước submit

**Việc làm**: chạy toàn bộ [`05-TEST-PLAN.md`](./05-TEST-PLAN.md) và hoàn thành [`04-PLAY-CONSOLE-CHECKLIST.md`](./04-PLAY-CONSOLE-CHECKLIST.md).

**Cổng chặn (không được bỏ qua)**
- [ ] Toàn bộ 8 P0 đã đóng, mỗi P0 có bằng chứng trong `evidence/`.
- [ ] Toàn bộ 14 P1 đã đóng hoặc có accept-risk được phê duyệt bằng văn bản.
- [ ] Test plan pass 100% trên tối thiểu 3 thiết bị (Android 13 / 14 / 15).
- [ ] Data Safety form hoàn thành và **được đối chiếu với code thực tế** (không tự khai theo cảm tính).
- [ ] Privacy policy + Terms + Data deletion URL đều trả 200 từ mạng ngoài.
- [ ] Bản build là **release + minify**, không phải debug.
- [ ] Đã test trên **Internal testing track** trước khi lên Closed/Open testing.

---

# PHỤ LỤC A — Thứ tự thực thi gợi ý theo tuần

| Tuần | Công việc | Người |
|---|---|---|
| **1** | Phase 0 đầy đủ · Quyết định Đường A/B cho voice · Quyết định business thanh toán (Bước 2.0) · **Kick-off backend**: endpoint verify Play Billing + xóa tài khoản thật + content reports | Cả team |
| **2** | Bước 1.1 (hoặc 1.2–1.4) · Bước 1.5, 1.6, 1.7 · Bước 4.1, 4.2 | Dev 1 |
| **2** | Bước 2.1, 2.2 · Backend 2.3 | Dev 2 + BE |
| **3** | Bước 2.4, 2.5 · Bước 4.3, 4.4 | Dev 1 + Dev 2 |
| **3** | Bước 3.1 (báo cáo nội dung AI) | Dev 2 |
| **4** | Bước 3.2 (xóa tài khoản + trang web) · Bước 4.5–4.10 | Dev 1 + BE + Web |
| **5** | Bước 3.3 (viết lại pháp lý) · Bước 4.11, 4.12 | Dev 1 + Legal |
| **5** | Bước 5.1–5.4 (build hardening) | Dev 2 |
| **6** | Bước 5.5–5.8 · Chạy [`05-TEST-PLAN.md`](./05-TEST-PLAN.md) đầy đủ | Cả team |
| **7** | Hoàn thành [`04-PLAY-CONSOLE-CHECKLIST.md`](./04-PLAY-CONSOLE-CHECKLIST.md) · Internal testing · Sửa lỗi phát sinh · Submit Closed Testing | Cả team |

---

# PHỤ LỤC B — Chiến lược MVP thu hẹp (nếu cần release sớm)

Nếu áp lực thời gian cao, có thể cắt phạm vi v1 để giảm từ **8 blocker xuống 3** và từ ~40 ngày xuống ~12 ngày:

| Cắt gì | Đóng được blocker nào | Tiết kiệm |
|---|---|---|
| **Cắt voice** — chỉ phỏng vấn bằng text (Bước 1.1) | P0-02, P0-03 (và P1-09) | ~8 ngày |
| **Cắt thanh toán trong app** — app v1 hoàn toàn miễn phí, chỉ gói FREE; xóa màn Pricing | P0-01 | ~8 ngày |
| **Cắt xuất dữ liệu** — ẩn nút, giữ kênh email `support@nexora.vn` trong chính sách | P0-05 | ~1 ngày |
| **Cắt quản lý avatar** — ẩn 2 nút (Bước 1.6 Lựa chọn B) | P0-06 | ~2 ngày |

**Còn lại phải làm (không thể cắt):**
1. **P0-04** — Báo cáo nội dung AI: **bắt buộc**, không có cách nào lách. (~2–3 ngày)
2. **P0-07** — Xóa tài khoản + web URL: **bắt buộc** với app có account. (~2 ngày)
3. **P0-08** — Pháp lý khớp thực tế: **bắt buộc**, và sau khi cắt nhiều thứ thì việc viết lại còn quan trọng hơn. (~1–2 ngày)
4. Các P1 ảnh hưởng an toàn dữ liệu: **4.1** (log leak), **4.3** (session persistence), **4.4** (auth guard), **5.1** (minify). (~4 ngày)

**Tổng MVP thu hẹp**: ~12–14 người-ngày.

### ⚠️ Cảnh báo về cách cắt

- **Nếu cắt thanh toán**: phải xóa **toàn bộ** màn Pricing và mọi dẫn dắt tới nó. Không được để "sắp ra mắt" hay link ra web — Play vẫn coi là dẫn user ra cổng ngoài.
- **Nếu cắt voice**: store listing, screenshot, và mô tả app **không được** nhắc tới tính năng giọng nói. Quyền `RECORD_AUDIO` phải bị xóa khỏi manifest.
- **Không cắt nửa vời**: để lại nút disabled hoặc màn hình "đang phát triển" còn tệ hơn là xóa hẳn — đó chính là "broken functionality".
