using Nexora.Business.Ai;

namespace Nexora.IntegrationTests;

/// <summary>
/// Test-project-only provider used to exercise state/idempotency invariants without network calls.
/// It is never registered by the application.
/// </summary>
internal sealed class TestAiProvider : IAiProvider
{
    public string ModelVersion => "test-gemini-model";

    public Task<T> GenerateStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var behavioral = request.UntrustedInput.Contains("interview-type: behavioral", StringComparison.OrdinalIgnoreCase);
        object result = typeof(T) switch
        {
            var type when type == typeof(GeneratedQuestion) => new GeneratedQuestion(
                request.Purpose == "interview.followup"
                    ? "Bạn sẽ cải thiện kết quả đó như thế nào nếu làm lại?"
                    : behavioral
                        ? "Hãy kể về một tình huống bạn giải quyết vấn đề khó trong vai trò này."
                        : "Explain dependency injection."),
            var type when type == typeof(AnswerEvaluation) => new AnswerEvaluation(
                [
                    new RubricScore("correctness", 75, "Câu trả lời nêu được cách xử lý."),
                    new RubricScore("structure", 70, "Câu trả lời có trình tự cơ bản."),
                    new RubricScore("completeness", 65, "Cần bổ sung kết quả định lượng."),
                    new RubricScore("clarity", 80, "Diễn đạt rõ và dễ theo dõi.")
                ],
                "Hãy thêm bối cảnh, hành động cá nhân và kết quả đo được.",
                behavioral ? new StarEvaluation(
                    true,
                    null,
                    new StarComponentEvaluation(80, true, "Có nêu bối cảnh vấn đề.", "Bối cảnh rõ."),
                    new StarComponentEvaluation(70, true, "Có trách nhiệm xử lý.", "Nên tách rõ trách nhiệm cá nhân hơn."),
                    new StarComponentEvaluation(75, true, "Có hành động phân tích và phối hợp.", "Hành động cá nhân tương đối rõ."),
                    new StarComponentEvaluation(45, false, string.Empty, "Cần nêu kết quả cụ thể hơn."),
                    ["result"],
                    ["Có hành động xử lý rõ"],
                    ["Kết thúc câu trả lời bằng kết quả và tác động cụ thể."]) : new StarEvaluation(false, null, null, null, null, null, [], [], [])),
            var type when type == typeof(InterviewReportOutput) => new InterviewReportOutput(
                [
                    new RubricScore("correctness", 75, "Transcript cho thấy hướng giải quyết phù hợp."),
                    new RubricScore("structure", 70, "Các ý có trình tự nhưng STAR chưa đầy đủ."),
                    new RubricScore("completeness", 65, "Transcript thiếu một số kết quả định lượng."),
                    new RubricScore("clarity", 80, "Câu trả lời rõ ràng và tập trung.")
                ],
                ["Diễn đạt rõ ràng"],
                ["Thiếu kết quả định lượng"],
                ["Luyện trả lời theo STAR"]),
            _ => throw new InvalidOperationException($"Test provider does not support {typeof(T).Name}.")
        };
        return Task.FromResult((T)result);
    }
}
