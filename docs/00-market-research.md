# Nghiên cứu sản phẩm tương tự và định vị Nexora

**Status:** Approved product-positioning baseline  
**Last updated:** 2026-08-21  
**Ngày nghiên cứu:** 20/08/2026 · **Mục đích:** xác định scope MVP có giá trị nhưng không sao chép toàn bộ tính năng của các sản phẩm lớn.

## 1. Nguồn tham chiếu

| Sản phẩm | Điều quan sát được từ nguồn chính thức | Hàm ý cho Nexora |
| --- | --- | --- |
| [Google Interview Warmup](https://grow.google/certificates/interview-warmup/) | Luồng hiệu quả là: trả lời → transcript → insight → trả lời lại; insight thiên về từ khoá, talking point và từ lặp. Google nhấn mạnh dữ liệu trả lời được giữ riêng tư. | MVP phải có vòng lặp luyện tập và feedback hành động được, không chỉ chấm điểm tổng. Privacy là yêu cầu sản phẩm. |
| [Yoodli Interview Practice](https://support.yoodli.ai/en/articles/9550465-practice-with-yoodli) | Cho chọn role/công ty/thái độ interviewer, question bank và follow-up động; sau phiên có analysis. | Interview context nên gồm role, seniority, JD/CV, loại phỏng vấn và độ khó; cần lưu rubric để feedback nhất quán. |
| [Yoodli Feedback](https://e.yoodli.ai/platform/ai-feedback) | Tách feedback nội dung với delivery (pacing, filler words, clarity) và dùng rubric có thể cấu hình. | Điểm Nexora phải giải thích được theo tiêu chí; delivery metrics chỉ phát hành khi transcript/audio đủ tin cậy. |
| [Final Round AI](https://www.finalroundai.com/) | Kết hợp CV + job details để cá nhân hoá mock interview và post-session report. | Liên kết CV/JD/session/report là luồng giá trị cốt lõi nên ưu tiên DB/API trước các module phụ. |

## 2. Khoảng trống và định vị đề xuất

Nexora không nên cạnh tranh bằng “stealth copilot” cho phỏng vấn thật. Tính năng đó khó kiểm soát rủi ro đạo đức, chính sách tuyển dụng và yêu cầu desktop/audio phức tạp. Định vị MVP:

> Trợ lý luyện phỏng vấn tiếng Việt, cá nhân hoá theo CV và JD, giúp người dùng thực hành trong môi trường an toàn và biết chính xác cách cải thiện ở phiên tiếp theo.

Khác biệt có thể xây được với team nhỏ:

- Hỗ trợ tiếng Việt tự nhiên và rubric phù hợp các role phổ biến tại Việt Nam (BA, PM, Marketing, Sales, IT).
- Liên kết một hành trình: CV/JD → question plan → mock interview → STAR/case practice → report → mục tiêu luyện tiếp.
- Feedback có căn cứ: nêu evidence từ transcript, rubric criteria, gợi ý hành động và câu trả lời luyện lại; không đưa “điểm AI” không giải thích.
- Privacy-by-default: audio/video là opt-in; transcript/CV có thời hạn lưu và xoá được.

## 3. Scope theo mức ưu tiên

SRS dùng Must/Should/Could làm nguồn ưu tiên implementation chính thức. Bảng nghiên cứu này ánh xạ sang cùng ý nghĩa và không tự tạo launch gate mới.

| Mức | Bao gồm | Lý do |
| --- | --- | --- |
| Must — production MVP | Auth/account, plan/entitlement/quota, CV/JD text, text mock interview, persistence, evidence/rubric report, payment sandbox/provider boundary, owner security, basic dashboard/history, observability/privacy/recovery. | Hoàn chỉnh vòng lặp giá trị và có thể vận hành an toàn. |
| Should — theo capacity MVP | STAR, scenario/case practice, adaptive follow-up, report export và question-bank improvements. | Có giá trị nhưng không mặc định là launch blocker. |
| Could / post-MVP | Speech-to-text/delivery metrics; audio/video recording chỉ khi opt-in; live-call integration, team/coach workspace. | Tăng rủi ro privacy, chi phí và vận hành. Covert live copilot vẫn là non-goal. |

## 4. Nguyên tắc sản phẩm rút ra

1. **Practice first:** sản phẩm dùng để luyện và phản hồi sau phiên; không hứa hẹn gian lận hay hỗ trợ ẩn trong phỏng vấn thật.
2. **Personalised but bounded:** chỉ dùng CV/JD mà user chọn gắn vào session; không tự dùng dữ liệu cũ ngoài mục đích đã nêu.
3. **Actionable feedback:** mỗi điểm yếu phải có evidence, đề xuất và bước luyện tiếp theo.
4. **Honest AI:** feedback là coaching aid, không phải đánh giá tuyển dụng chính thức; hiển thị model/prompt version nội bộ cho audit.
5. **Cost-aware:** quota tính theo phiên/job; xử lý dài bất đồng bộ; limit rõ ràng theo plan.
