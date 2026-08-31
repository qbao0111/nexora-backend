using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.Integrations.Ai;

public sealed class FakeAiProvider : IAiProvider
{
    public Task<T> GenerateStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object result = typeof(T) switch
        {
            var type when type == typeof(GeneratedQuestion) => new GeneratedQuestion(
                request.Purpose == "interview.followup"
                    ? "Bạn sẽ cải thiện kết quả đó như thế nào nếu làm lại?"
                    : "Hãy kể về một tình huống bạn giải quyết vấn đề khó trong vai trò này."),
            var type when type == typeof(ResumeAnalysisOutput) => new ResumeAnalysisOutput(
                ["Kinh nghiệm được trình bày rõ ràng"],
                ["Cần thêm kết quả định lượng phù hợp JD"],
                ["Bổ sung bằng chứng cụ thể cho các kỹ năng chính"]),
            var type when type == typeof(ResumeProfile) => new ResumeProfile(
                "Backend developer focused on reliable REST APIs and data-backed features.",
                ["C#", ".NET", "PostgreSQL", "REST API"],
                [new ResumeExperience("Nexora Labs", "Backend Developer", "2024", null,
                    ["Built REST APIs and improved query reliability."])],
                [new ResumeEducation("University of Technology", "Computer Science", "2020", "2024", [])],
                [new ResumeProject("Interview practice platform", "Backend developer", ["C#", ".NET", "PostgreSQL"],
                    ["Implemented a modular monolith API."])],
                ["AWS Certified Cloud Practitioner"],
                ["Vietnamese", "English"]),
            var type when type == typeof(AnswerEvaluation) => new AnswerEvaluation(
                [
                    new RubricScore("correctness", 75, "Câu trả lời nêu được cách xử lý."),
                    new RubricScore("structure", 70, "Câu trả lời có trình tự cơ bản."),
                    new RubricScore("completeness", 65, "Cần bổ sung kết quả định lượng."),
                    new RubricScore("clarity", 80, "Diễn đạt rõ và dễ theo dõi.")
                ],
                "Hãy thêm bối cảnh, hành động cá nhân và kết quả đo được."),
            var type when type == typeof(InterviewReportOutput) => new InterviewReportOutput(
                [
                    new RubricScore("correctness", 75, "Transcript cho thấy hướng giải quyết phù hợp."),
                    new RubricScore("structure", 70, "Các ý có trình tự nhưng STAR chưa đầy đủ."),
                    new RubricScore("completeness", 65, "Transcript thiếu một số kết quả định lượng."),
                    new RubricScore("clarity", 80, "Câu trả lời rõ ràng và tập trung.")
                ],
                ["Diễn đạt rõ ràng", "Nêu được hành động chính"],
                ["Thiếu kết quả định lượng"],
                ["Luyện trả lời theo STAR", "Chuẩn bị số liệu cho hai dự án gần nhất"]),
            _ => throw new InvalidOperationException($"Fake AI does not support {typeof(T).Name}.")
        };
        return Task.FromResult((T)result);
    }
}
