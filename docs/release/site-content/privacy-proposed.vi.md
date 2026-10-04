## 1. Dữ liệu Nexora có thể xử lý
Tùy chức năng bạn sử dụng, Nexora có thể xử lý thông tin tài khoản và xác thực, email, tên hiển thị, ảnh đại diện, hồ sơ nghề nghiệp, mục tiêu nghề nghiệp, CV và nội dung được trích xuất từ CV, mô tả công việc, câu trả lời phỏng vấn, kết quả đánh giá, báo cáo, dữ liệu luyện tập STAR hoặc tình huống, tín hiệu năng lực, lộ trình học tập, phản hồi sản phẩm, thông tin đơn hàng và dữ liệu phiên đăng nhập hoặc bảo mật.

Nexora chỉ nên yêu cầu dữ liệu cần thiết cho chức năng bạn đang sử dụng.

## 2. Mục đích xử lý
Dữ liệu được xử lý để xác thực tài khoản, cung cấp chức năng phân tích và luyện tập, duy trì lịch sử và trạng thái tiến bộ, quản lý quyền lợi gói, xử lý giao dịch, vận hành các tính năng giọng nói khi bạn chủ động sử dụng, gửi email cần thiết, bảo vệ tài khoản, xử lý yêu cầu hỗ trợ và duy trì an toàn hệ thống.

## 3. Xử lý bởi AI và dịch vụ giọng nói
Nexora lưu CV dưới dạng đối tượng riêng tư trên Cloudflare R2 và trích xuất văn bản PDF/DOCX tại máy chủ. Nexora không sử dụng Gemini OCR hoặc dịch vụ OCR bên ngoài; PDF chỉ chứa ảnh hoặc bản scan có thể không đọc được.

Khi bạn yêu cầu chức năng AI, văn bản CV đã trích xuất hoặc hồ sơ rút gọn, mô tả công việc, mục tiêu nghề nghiệp, câu trả lời và nội dung cần thiết được gửi tới DeepSeek API để cung cấp phân tích, phản hồi và luyện tập. Việc này là truyền nội dung tài liệu đã trích xuất, không phải tải nguyên tệp PDF/DOCX lên DeepSeek. Dữ liệu bạn tự đưa vào nội dung có thể chứa tên, email, số điện thoại hoặc địa chỉ; không gửi thông tin nhạy cảm không cần thiết hoặc dữ liệu của người khác khi chưa được phép.

Khi bạn chủ động sử dụng chức năng giọng nói và cấp quyền microphone, âm thanh được gửi tới Microsoft Azure Speech để chuyển thành văn bản; nội dung cần đọc được gửi tới Azure Speech để tổng hợp giọng nói. Bạn có thể chọn chế độ văn bản. Ứng dụng cố gắng dọn tệp âm thanh tạm sau xử lý; văn bản câu trả lời được lưu khi bạn nộp. Việc dọn tệp trên thiết bị khác với vòng đời dữ liệu tại nhà cung cấp.

Việc xử lý và lưu giữ tại nhà cung cấp phụ thuộc điều khoản áp dụng và cấu hình thực tế. Nexora không cam kết DeepSeek không lưu dữ liệu, không huấn luyện mô hình hoặc xóa ngay mọi bản sao. Việc công bố truyền dữ liệu không phải khẳng định nhà cung cấp sử dụng dữ liệu để huấn luyện.

Không nên xem phản hồi của nhà cung cấp AI hoặc giọng nói là dữ liệu xác minh độc lập về kinh nghiệm hoặc năng lực của bạn.

## 4. Hạ tầng và nhà cung cấp dịch vụ
Render vận hành máy chủ Nexora; Neon PostgreSQL lưu dữ liệu tài khoản và tính năng. Cloudflare R2 lưu CV và ảnh đại diện riêng tư. Resend xử lý địa chỉ email và nội dung email xác minh, khôi phục và xác minh yêu cầu xóa tài khoản. DeepSeek API và Microsoft Azure Speech xử lý nội dung trong phạm vi mô tả ở mục 3. Thanh toán trên website có thể được xử lý bởi nhà cung cấp được hiển thị tại bước thanh toán.

Tệp CV, ảnh đại diện và các tài sản riêng tư được lưu trong cơ chế lưu trữ có kiểm soát truy cập. Một số thao tác tải tệp có thể sử dụng URL ký tạm thời để trình duyệt truyền tệp trực tiếp tới kho lưu trữ mà không biến tệp thành tài nguyên công khai.

Ứng dụng Mobile trong bản phát hành mới không tích hợp Sentry hoặc tự động gửi báo cáo lỗi qua SDK phân tích. Điều này không phải khẳng định các bản ứng dụng cũ đã được thay đổi. Máy chủ và hạ tầng có thể xử lý nhật ký kỹ thuật, địa chỉ IP và thông tin yêu cầu để bảo mật, xử lý lỗi và vận hành; Sentry phía máy chủ phụ thuộc cấu hình. Điều này khác với phản hồi hoặc báo cáo nội dung bạn chủ động gửi.

Phạm vi xử lý dữ liệu của nhà cung cấp tuân theo hợp đồng áp dụng và cấu hình dịch vụ. Dữ liệu tài khoản, bảo mật hoặc hoạt động độc lập của nhà cung cấp không mặc nhiên có cùng phạm vi với nội dung người dùng do Nexora gửi để thực hiện dịch vụ. Nhà cung cấp cụ thể có thể thay đổi theo môi trường và cấu hình vận hành.

## 5. Phản hồi được chia sẻ công khai
Phản hồi sản phẩm của bạn không mặc nhiên trở thành nội dung công khai.

Việc hiển thị phản hồi trên trang công khai phụ thuộc vào trạng thái đồng ý chia sẻ và quy trình kiểm duyệt của Nexora. Bạn có thể thay đổi hoặc rút lại phản hồi theo các chức năng được cung cấp.

Trong ứng dụng Mobile mới, nhận xét không mặc định công khai. Nếu bạn cho phép hiển thị, nhận xét đã được duyệt có thể xuất hiện trên website cùng tên hiển thị và ảnh đại diện. Bạn có thể thay đổi lựa chọn hoặc xóa đánh giá.

## 6. Bảo mật tài khoản và phiên đăng nhập
Nexora áp dụng kiểm soát truy cập phía máy chủ, phân quyền đối với tài nguyên riêng tư và cơ chế quản lý phiên đăng nhập.

Bạn có thể xem và thu hồi các phiên đăng nhập được hỗ trợ trong phần cài đặt. Khi có yêu cầu xóa tài khoản, các phiên hoạt động liên quan được thu hồi theo quy trình của hệ thống.

Không có hệ thống trực tuyến nào có thể đảm bảo an toàn tuyệt đối; vì vậy bạn cũng cần bảo vệ thông tin đăng nhập và thiết bị của mình.

## 7. Quyền kiểm soát dữ liệu của bạn
Tùy chức năng hiện có, bạn có thể cập nhật hồ sơ, thay đổi hoặc gỡ ảnh đại diện, quản lý mục tiêu nghề nghiệp và CV, thu hồi phiên đăng nhập, tải bản xuất dữ liệu cốt lõi và gửi yêu cầu xóa tài khoản.

Bản xuất dữ liệu cốt lõi có thể bao gồm những nhóm dữ liệu như hồ sơ, thông tin CV, mô tả công việc, mục tiêu nghề nghiệp, lộ trình học, kết quả phân tích, lịch sử phỏng vấn và báo cáo, phản hồi sản phẩm và thông tin quyền lợi hoặc giao dịch liên quan.

## 8. Lưu giữ và xóa dữ liệu
Nexora lưu dữ liệu trong thời gian cần thiết để cung cấp chức năng đang sử dụng, duy trì tính toàn vẹn của giao dịch, bảo vệ hệ thống và đáp ứng các nghĩa vụ vận hành hoặc pháp lý có liên quan.

Khi yêu cầu xóa được chấp nhận, tài khoản bị chặn truy cập, các phiên liên quan được thu hồi và yêu cầu được xử lý theo hàng đợi; chấp nhận yêu cầu không phải cam kết hoàn tất ngay. Khi hoàn tất, Nexora xóa dữ liệu nghề nghiệp, CV và ảnh đại diện thuộc tài khoản, nội dung luyện tập và báo cáo thuộc quy trình xóa; thông tin nhận diện tài khoản được loại bỏ hoặc thay thế và tài khoản không thể đăng nhập. Lượt tải lên còn hiệu lực hoặc lỗi lưu trữ có thể làm việc hoàn tất chậm hơn. Trạng thái thất bại hoặc chưa hoàn tất không đồng nghĩa đã xóa xong.

Bản ghi tài khoản với mã định danh ổn định vẫn được giữ để bảo đảm liên kết giao dịch. Mã tài khoản giả danh có thể còn gắn với đơn hàng, sự kiện thanh toán, quyền lợi, lịch sử sử dụng và kiểm toán nhằm đối soát, duy trì tính toàn vẹn hạn mức, xử lý tranh chấp, bảo mật và nghĩa vụ pháp luật. Đây không phải ẩn danh không thể liên kết; không phải mọi dữ liệu sử dụng hoặc kiểm toán đều được tự động xóa khi xóa tài khoản.

Những bản ghi được phép giữ theo pháp luật, tranh chấp, chống gian lận, kế toán hoặc an ninh có thể được áp dụng cơ chế lưu giữ hợp pháp. Cơ chế này đối với tác vụ dọn dữ liệu định kỳ không phải cơ chế hủy yêu cầu xóa tài khoản hoặc cam kết xóa dữ liệu của nhà cung cấp.

Hệ thống có khả năng dọn một số thông tin xác minh hết hạn và thông tin yêu cầu xóa đã hoàn tất đủ 12 tháng, nhưng mặc định việc dọn định kỳ bị tắt hoặc chỉ báo cáo; việc áp dụng còn phụ thuộc phê duyệt, cấu hình, tồn đọng, lỗi và ngoại lệ lưu giữ. Mốc 12 tháng là điều kiện có thể được xem xét dọn, không phải bảo đảm ngày xóa. Các mục tiêu nhật ký 30 ngày và dữ liệu sử dụng không phục vụ kế toán 90 ngày chưa được thực thi như thời hạn xóa bảo đảm. Không cam kết mọi dữ liệu tự động bị xóa theo các mốc này; dữ liệu sử dụng gắn với sổ cái tài chính và kiểm toán vẫn được giữ trong phạm vi cần thiết.

Bản sao lưu, bản sao lịch sử và dữ liệu tại nhà cung cấp có vòng đời riêng. Xóa dữ liệu đang hoạt động trong Nexora không tự động chứng minh mọi bản sao ngoài hệ thống đã bị xóa. Nexora không cam kết một thời hạn chung chưa được xác minh cho Neon, Render, R2, Resend, DeepSeek, Azure Speech hoặc các bản sao đã gửi tới nhà cung cấp trước đây.

Bạn có thể yêu cầu xóa trong Cài đặt tài khoản hoặc tại https://www.nexorainterview.io.vn/account-deletion không cần đăng nhập. Liên kết xác minh email chỉ dùng một lần, hết hạn sau 30 phút; đây không phải thời gian ân hạn 30 ngày. Chỉ khi bạn chủ động xác nhận và máy chủ chấp nhận thì yêu cầu mới được gửi. Không có chức năng hủy yêu cầu hoặc khôi phục bằng đăng nhập lại.

## 9. Thay đổi chính sách và liên hệ
Khi chính sách này thay đổi, bản được công bố sẽ hiển thị ngày hiệu lực hoặc ngày cập nhật tương ứng.

Nếu có câu hỏi về dữ liệu cá nhân hoặc chính sách bảo mật, liên hệ nexorainterview.vn@gmail.com.
