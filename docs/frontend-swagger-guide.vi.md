# Nexora — Setup backend và test API bằng Swagger cho teammate FE

Cập nhật: 01/09/2026. Dành cho môi trường Development, dùng Gemini với CV/JD thật do project owner cung cấp.

## 1. Cần chuẩn bị gì?

| Thành phần | Cần làm |
| --- | --- |
| Git | Cài Git, có quyền truy cập repository. |
| .NET SDK | Cài .NET 10 SDK; kiểm tra phiên bản được chấp nhận trong `global.json` của repo. Hiện pin `10.0.203`, cho phép roll-forward `latestFeature`. |
| PowerShell 7 | Các script dưới đây dùng `pwsh`, không phải Windows PowerShell 5.1. |
| Database DEV | Xin qb connection string Npgsql của nhánh Neon **development** qua kênh riêng. Không dùng database production. |
| AI Development | Dùng `Gemini` thật với API key/model trong user-secrets; không đưa secret vào FE. |
| AI automated tests | Chỉ các test invariant cần thiết mới thay adapter trong test project; runtime application luôn là Gemini. |
| File test | Chọn PDF/DOCX hợp lệ của project owner. Mặc định tối đa 10 MiB. |

Không cần cài PostgreSQL/Docker trên máy nếu dùng Neon DEV.
Chạy lệnh backend tại thư mục có `Nexora.slnx`. Không chạy trong repo frontend.

## 2. Clone và kiểm tra baseline

```powershell
git clone https://github.com/qbao0111/nexora-backend.git
cd nexora-backend
git switch main
git pull --ff-only origin main
dotnet --version
pwsh --version
dotnet tool restore
dotnet restore Nexora.slnx
dotnet build Nexora.slnx --no-restore
dotnet test Nexora.slnx --no-build
```

Nếu đã clone, không clone lại. Kiểm tra `git status` và giữ nguyên phần đang sửa trước khi đổi branch/pull.
Test mặc định dùng provider double chỉ trong test project và SQLite in-memory, không cần kết nối Neon; test pass không có nghĩa DB DEV đã được cấu hình.

## 3. Cấu hình secret riêng trên từng máy

Mở PowerShell 7, trong repo backend:

```powershell
$devConnection = Read-Host "Nhap connection string Neon DEVELOPMENT do qb cap"
dotnet user-secrets set "ConnectionStrings:Postgres" "$devConnection" --project src/Nexora.Api
$devConnection = $null
dotnet user-secrets set "Ai:Gemini:Model" "YOUR_CONFIGURED_MODEL" --project src/Nexora.Api
dotnet user-secrets set "Ai:Gemini:ApiKey" "YOUR_DEVELOPMENT_KEY" --project src/Nexora.Api
```

Nhập connection tại prompt giúp tránh ghi nguyên giá trị vào lịch sử câu lệnh. Không chụp màn hình lúc nhập.
Thay placeholder bằng giá trị thật trong terminal riêng; không commit, không gửi vào chat/PR/log. Nếu muốn tránh ghi key vào command history, set key từ prompt vào biến tạm rồi xóa biến sau khi lưu.
Dạng connection phải là Npgsql key/value (`Host=...;Database=...;Username=...;Password=...;SSL Mode=Require`), không phải URL `postgresql://...`.
Script Neon kiểm tra đuôi host `.neon.tech` và SSL; teammate vẫn phải tự xác nhận đúng nhánh DEV với qb. Nếu Neon đang mở branch `production`, connection secret hiện tại đang trỏ nhầm DB: dừng API/Worker, lấy connection của branch `development` trong Neon rồi set lại secret. Không tạo user test mới trên `production` để “thử nhanh”.

API và Worker dùng chung user-secrets ID `Nexora.LocalDevelopment`, nên chỉ cần set một lần.
User-secrets nằm ngoài git nhưng không phải kho mã hóa production; không chia sẻ file secrets.
DEV đã có signing key và fake-webhook key mẫu trong cấu hình development; tuyệt đối không tái dùng chúng ở production.

Chỉ kiểm tra tên secret, không in giá trị để gửi người khác:

```powershell
dotnet user-secrets list --project src/Nexora.Api |
    ForEach-Object { ($_ -split '\s*=\s*', 2)[0].Trim() }
```

Kết quả cần có `ConnectionStrings:Postgres`, `Ai:Gemini:Model` và `Ai:Gemini:ApiKey`. Model phải là API ID do maintainer cung cấp, không phải tên hiển thị. Nếu AI bật mà thiếu key/model, API và Worker dừng với lỗi cấu hình rõ ràng.

Không đưa connection string, JWT, refresh token hoặc API key vào FE, source code, ảnh chụp, PR hay log chia sẻ.

## 4. Chạy API + Worker

```powershell
pwsh ./scripts/run-development.ps1
```

Script build, áp dụng migration lên DB đã cấu hình, chạy API + Worker và chờ readiness. Script tự ép API và Worker dùng chung đường dẫn tuyệt đối `.nexora-local/storage`, nên không còn lệch file khi chạy từ Visual Studio/terminal khác nhau.
Chỉ dùng DB DEV được qb cho phép migrate. Không chạy script reset/drop database.
Giữ terminal này mở; `Ctrl+C` dừng cả hai process.

| Mục | Địa chỉ mặc định |
| --- | --- |
| Swagger UI | http://localhost:5088/swagger |
| OpenAPI JSON | http://localhost:5088/openapi/v1.json |
| API base | http://localhost:5088/api/v1 |
| Readiness có kiểm tra DB | http://localhost:5088/api/v1/health |
| Liveness của process | http://localhost:5088/health/live |
| Log | `.nexora-local/logs/` trong repo backend |

Swagger chỉ có ở **Development**. Testing chỉ có OpenAPI JSON; Staging/Production không mở cả hai.
Swagger vào được chưa đủ: readiness phải healthy, và Worker phải đang chạy để xử lý CV/AI.
Script ghi lại log của phiên mới; sao lưu phần log cần điều tra trước khi khởi động lại.

## 5. Xác nhận Gemini trước khi test FE

Giữ API và Worker chạy bằng `pwsh ./scripts/run-development.ps1`, rồi kiểm tra `http://localhost:5088/api/v1/health`. Development dùng Gemini thật; nếu thiếu `Ai:Gemini:ApiKey` hoặc `Ai:Gemini:Model`, startup sẽ dừng với lỗi cấu hình. Nếu Gemini trả `400`, kiểm tra model ID do maintainer cung cấp; nếu `401/403`, kiểm tra key/quota nhưng không in key ra log.

## 6. Cách dùng Swagger và đăng nhập

1. Mở Swagger, chọn endpoint, bấm **Try it out**.
2. Sửa request body, bấm **Execute**.
3. Đọc **Server response** (status và response body thật), không nhầm với response mẫu phía dưới.

### Tạo tài khoản test

`POST /api/v1/auth/register`:

```json
{
  "email": "fe-swagger-01@example.test",
  "password": "Manual-Test!2026",
  "displayName": "FE Swagger Test"
}
```

Email và password trên chỉ là ví dụ test, không dùng cho tài khoản thật. Đổi email nếu bị trùng.
Kỳ vọng `201`, lấy `data.accessToken`. Nếu đã có tài khoản, dùng `POST /api/v1/auth/login` với email/password đó.

### Authorize

Bấm **Authorize** ở đầu trang → dán **chỉ accessToken**, không thêm chữ `Bearer` → Authorize → Close.
Swagger tự thêm `Authorization: Bearer <token>` cho endpoint được bảo vệ.
Gọi `GET /api/v1/me`: kỳ vọng `200`, thông tin user nằm trong `data`.

Token không được Swagger lưu qua reload. Khi reload/hết hạn, login rồi Authorize lại.
Không gửi token hay ảnh response đăng nhập cho người khác.

### Idempotency-Key

Các POST checkout, resume-analysis, bắt đầu interview, gửi answer, complete và yêu cầu xóa có ô header này.
Tạo một UUID:

```powershell
[guid]::NewGuid().ToString()
```

Một ý định thao tác mới → một key mới. Retry cùng ý định → giữ nguyên key **và body**.
Không đổi key liên tục chỉ vì mạng timeout; không dùng cùng key cho hai payload khác nhau.
Không cần key cho GET, register/login, presign, finalize CV hoặc tạo JD.

### Shortcut Development: chọn file + nhập JD

Để debug nhanh trên Swagger, dùng `POST /api/v1/dev/resume-analysis` thay cho 5 bước upload thủ công:

- `multipart/form-data`: chọn `File` (PDF/DOCX) và nhập `JobDescription`.
- Điền một `Idempotency-Key` mới (có thể dùng UUID); không cần nhập `size`, `resumeId` hay `jobDescriptionId`.
- Endpoint tự upload, trích xuất CV bằng extractor PDF/DOCX thật, tạo JD và queue analysis.
- Kết quả trả về `data.resume`, `data.jobDescription`, `data.analysis`; lưu `data.analysis.id` rồi poll `GET /api/v1/resume-analyses/{id}` như bên dưới.
- Route chỉ tồn tại ở **Development**, không được expose ở Staging/Production. API vẫn cần `Authorize` bằng access token.

FE production nên dùng flow chuẩn ở mục kế tiếp; FE có thể lấy `file.size` trực tiếp từ `File` khi gọi presign, không yêu cầu người dùng tự gõ số byte.

## 7. Test CV → JD → Analysis theo đúng thứ tự

Dùng cùng tài khoản/token cho toàn bộ luồng.

| Bước | Endpoint | Kỳ vọng / lấy giá trị |
| --- | --- | --- |
| 1 | POST `/uploads/presign` | `200`; lưu `data.token`, `data.uploadUrl`. |
| 2 | PUT `/uploads/{token}` | Gửi raw file; `204`, không có JSON body. |
| 3 | POST `/resumes` | `201`; lưu `data.id` thành resumeId. |
| 4 | POST `/job-descriptions` | `201`; lưu `data.id` thành jobDescriptionId. |
| 5 | POST `/resume-analyses` | Có Idempotency-Key; `201`; lưu analysisId. |
| 6 | GET `/resume-analyses/{id}` | `200`; poll đến `completed` hoặc `failed`. |

Các route ngắn trong bảng đều có prefix `/api/v1`.

### Bước 1 — Presign

Lấy đúng kích thước file tính bằng byte:

```powershell
(Get-Item -LiteralPath "C:\path\resume-test.pdf").Length
```

Thay `12345` bằng kết quả thật:

```json
{
  "fileName": "resume-test.pdf",
  "contentType": "application/pdf",
  "size": 12345
}
```

DOCX dùng `application/vnd.openxmlformats-officedocument.wordprocessingml.document`.
Không dùng dung lượng hiển thị làm tròn KB/MB.

### Bước 2 — Upload file

Mở `PUT /api/v1/uploads/{token}` → Try it out:

- `token`: dán `data.token` từ presign.
- Chọn content type PDF hoặc DOCX khớp bước 1.
- **Choose File**: chọn đúng file đã khai báo.
- Execute → kỳ vọng `204`.

Đây là **raw binary**, không phải multipart/form-data, JSON, base64 hay đường dẫn file dạng text.
Token upload là capability tạm thời: không chia sẻ. URL trả về tương đối với API origin, không phải origin FE.
Intent mặc định hết hạn sau 10 phút; restart API làm mất intent chưa hoàn tất.

Nếu cần dùng Postman: PUT `http://localhost:5088{uploadUrl}`, Body → binary → chọn file, Content-Type tương ứng.

### Bước 3 — Finalize CV

`POST /api/v1/resumes`:

```json
{ "uploadToken": "TOKEN_TU_BUOC_1" }
```

Thay placeholder bằng token thật. CV ban đầu là `uploaded`; Worker sẽ xử lý bằng extractor thật cho PDF/DOCX. Poll `GET /api/v1/resumes/{id}`: `uploaded → extracting → ready`; tài liệu cần fallback sẽ hiện `ocr_fallback`; lỗi cuối là `failed` kèm `errorCode = RESUME_EXTRACTION_FAILED` và thông báo an toàn.

### Bước 4 — Tạo JD

`POST /api/v1/job-descriptions`:

```json
{
  "title": "Junior Frontend Developer",
  "content": "Build and maintain React and TypeScript interfaces, integrate REST APIs, write automated tests, and collaborate with backend engineers."
}
```

### Bước 5 — Tạo analysis

`POST /api/v1/resume-analyses`, điền Idempotency-Key:

```json
{
  "resumeId": "UUID_CV_TU_BUOC_3",
  "jobDescriptionId": "UUID_JD_TU_BUOC_4"
}
```

Nếu `409` và `error.code = RESUME_NOT_READY`: đợi 2–3 giây, retry **cùng key/body**, giới hạn khoảng 60 giây.
Nếu vẫn lỗi, kiểm tra Worker/log; không retry vô hạn, không retry mọi lỗi 409.
Nếu `201`, lưu `data.id`.

### Bước 6 — Poll kết quả

`GET /api/v1/resume-analyses/{analysisId}`:

- `queued/processing`: tiếp tục đợi, tăng khoảng poll lên 3–5 giây.
- `completed`: hiển thị kết quả backend.
- `failed`: dừng, hiển thị lỗi an toàn và requestId nếu có.

Với Development, kết quả phân tích được tạo bởi Gemini thật và nhận text trích xuất thật từ PDF/DOCX. PDF scan hoặc tài liệu có text local không đủ chất lượng sẽ tự vào `ocr_fallback`; Gemini trả text + profile trong một lần document-understanding. Nếu fallback vẫn không usable, dừng ở `failed` và hiển thị lỗi an toàn.

## 8. Test plan, fake payment và interview bằng Gemini

1. `GET /api/v1/plans` → lấy `prices[].id` của gói trả phí từ server.
2. `POST /api/v1/checkout-sessions`, Idempotency-Key mới, body `{ "planPriceId": "UUID_PRICE" }`.
3. Lấy `data.orderId`, mở terminal backend khác:

```powershell
pwsh ./scripts/complete-fake-payment.ps1 -OrderId "UUID_ORDER"
```

4. `GET /api/v1/me` → kiểm tra entitlement/quota mới. Đây là thanh toán giả, không thu tiền; không gọi webhook trực tiếp từ FE.
5. `POST /api/v1/interviews`, key mới:

```json
{
  "role": "Frontend Developer",
  "seniority": "junior",
  "interviewType": "behavioral",
  "difficulty": "medium"
}
```

6. Poll `GET /api/v1/interviews/{id}`: `starting → active/failed`.
7. Khi `active`, lấy questionId thực từ response; POST `/interviews/{id}/answers` với key riêng cho mỗi answer:

```json
{
  "questionId": "UUID_QUESTION",
  "content": "Tôi làm rõ yêu cầu, triển khai component theo từng phần nhỏ và kiểm chứng bằng test tự động.",
  "durationSeconds": 45
}
```

8. Dùng `nextQuestion` cho lượt sau, dừng khi `isComplete=true`.
9. POST `/interviews/{id}/complete`, key mới, không body → `202`.
10. Poll session/report đến hoàn tất. `GET /interviews/{id}/report` trả report; kiểm tra thêm `GET /dashboard`.

Chỉ coi report `404` là đang chờ nếu biết session đang `completing`. Sau terminal failure dừng poll và xử lý lỗi.
Không tự test endpoint xóa account trên tài khoản dùng chung; endpoint đó thu hồi session và xóa/anonymize dữ liệu.

## 9. Nối frontend thực tế

Nếu FE dùng Vite, tạo `.env.local` trong **repo FE**:

```dotenv
VITE_API_ORIGIN=http://localhost:5088
```

Restart Vite sau khi đổi env. FE dùng localhost:3000 hoặc localhost:5173.
Không nhầm port FE `3000` với port API `5088`.

```javascript
const apiOrigin = import.meta.env.VITE_API_ORIGIN;
const response = await fetch(`${apiOrigin}/api/v1/me`, {
  headers: { Authorization: `Bearer ${accessToken}` },
  credentials: "include"
});
const body = await response.json();
if (!response.ok) {
  throw new Error(`${body.error.code}: ${body.error.message}`);
}
const me = body.data;
```

Giữ accessToken trong memory. Refresh token ở cookie HttpOnly; không đọc hay lưu nó trong localStorage.
`POST /auth/refresh` cần cookie và `credentials: "include"`; gom refresh vào một lock, retry request gốc tối đa một lần.
Cookie refresh có Secure/SameSite; dùng `localhost` nhất quán và kiểm tra browser thực sự chấp nhận/gửi cookie. Nếu trình duyệt chặn Secure cookie trên HTTP local, dùng HTTPS dev tin cậy; không tắt Secure cho production.
Swagger cùng origin API nên test được Swagger **không chứng minh** CORS/cookie từ FE đã đúng.

## 10. Bảng xử lý lỗi nhanh

| Hiện tượng | Cần kiểm tra |
| --- | --- |
| Swagger `404` | Pull main mới, build lại, chạy script Development; đúng port 5088. Không mở Swagger ở production. |
| Không mở được API | Terminal còn chạy không, đúng port không, log API có lỗi startup không. |
| Thiếu `ConnectionStrings:Postgres` | Secret phải set trên chính máy teammate. API thường không khởi động, không phải lỗi 409 trực tiếp. |
| DB sai/mất kết nối | Readiness không healthy, log lỗi DB; xác nhận connection/migration với qb. Nếu Neon hiển thị branch `production`, thay secret bằng connection branch `development` trước khi chạy lại. |
| `401 UNAUTHENTICATED` | Login, Authorize lại token chưa hết hạn; chỉ dán token, không gõ Bearer hai lần. |
| Refresh `401` | Cookie thiếu/hết hạn, sai host hoặc chưa include credentials; login lại. |
| `400 IDEMPOTENCY_KEY_REQUIRED` | Điền header key cho đúng mutation. |
| `409 IDEMPOTENCY_CONFLICT` | Cùng key nhưng payload khác; khôi phục payload cũ cho retry hoặc tạo ý định mới với key mới. |
| `409 RESUME_NOT_READY` | CV chưa ready hoặc extraction failed; kiểm tra Worker cùng DB/storage với API. |
| `400 INVALID_FILE` | Đúng PDF/DOCX, đúng size byte/MIME/signature; đừng đổi đuôi EXE thành PDF. |
| `404 UPLOAD_INTENT_INVALID` | Token sai/hết hạn/đã dùng/API vừa restart; presign mới và upload lại. |
| `404 UPLOAD_NOT_FOUND` | Chưa PUT xong, sai owner/token hoặc intent bị mất; làm lại chuỗi upload. |
| `403 QUOTA_EXCEEDED` | Dùng helper fake checkout trên DEV, refetch /me. |
| `429 RATE_LIMITED` | Đợi theo Retry-After; không spam request. |
| Job mắc `queued/starting/completing` | Worker phải chạy, cùng DB và storage; xem worker.stdout.log/worker.stderr.log. |
| Browser CORS | FE đúng origin cho phép; localhost khác 127.0.0.1, không chạy HTML bằng file://. |

Khi nhờ hỗ trợ, gửi: endpoint, HTTP status, `error.code`, `requestId`, thời điểm và mô tả bước tái hiện.
Che token, password, connection string, upload capability và nội dung CV/answers thật.

## 11. Checklist bàn giao FE

- [ ] Pull main; restore/build/test pass.
- [ ] Secret riêng từng máy; Gemini key/model đúng; đúng DB DEV.
- [ ] API healthy và Worker chạy.
- [ ] Swagger register/login → Authorize → /me pass.
- [ ] Presign → raw upload 204 → finalize 201 → JD → analysis completed.
- [ ] Fake checkout → entitlement → interview → answers → report pass.
- [ ] FE thực tế test login/refresh/CORS ở localhost:3000 hoặc 5173.
- [ ] Không hard-code price, quota, userId, score; không dùng mock state làm authority.
- [ ] Không commit secrets hoặc dữ liệu runtime.

Hướng dẫn này không thay thế các production gates DEC-01–04, test PostgreSQL/concurrency, security và staging. Gemini ở đây chỉ là development adapter; không bật traffic production.
Tài liệu nguồn: [repo Nexora Backend](https://github.com/qbao0111/nexora-backend), `docs/03-api-data-contract.md`, `docs/frontend-integration.md`, `docs/development-setup.md`.
