using System.Collections.Concurrent;
using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.IntegrationTests;

/// <summary>
/// Test-project-only provider used to exercise state/idempotency invariants without network calls.
/// It is never registered by the application.
/// </summary>
internal sealed class TestAiProvider : IAiProvider
{
    public string ModelVersion { get; set; } = "test-gemini-model";

    private readonly ConcurrentQueue<AiRequest> _invocations = new();
    private readonly ConcurrentDictionary<string, int> _callCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Queue<Func<AiRequest, object>>> _scriptedResponses = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AiRequest> Invocations => _invocations.ToArray();

    public int GetCallCount(string purpose) => _callCounts.TryGetValue(purpose, out var count) ? count : 0;
    public int TotalCalls => _invocations.Count;

    public void EnqueueResponse(string purpose, object responseOrException)
    {
        var queue = _scriptedResponses.GetOrAdd(purpose, _ => new Queue<Func<AiRequest, object>>());
        lock (queue)
        {
            queue.Enqueue(_ => responseOrException);
        }
    }

    public void EnqueueHandler(string purpose, Func<AiRequest, object> handler)
    {
        var queue = _scriptedResponses.GetOrAdd(purpose, _ => new Queue<Func<AiRequest, object>>());
        lock (queue)
        {
            queue.Enqueue(handler);
        }
    }

    public void Reset()
    {
        _invocations.Clear();
        _callCounts.Clear();
        _scriptedResponses.Clear();
    }

    public Task<T> GenerateStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _invocations.Enqueue(request);
        _callCounts.AddOrUpdate(request.Purpose, 1, (_, current) => current + 1);

        if (_scriptedResponses.TryGetValue(request.Purpose, out var queue))
        {
            Func<AiRequest, object>? handler = null;
            lock (queue)
            {
                if (queue.Count > 0)
                {
                    handler = queue.Dequeue();
                }
            }

            if (handler is not null)
            {
                var outcome = handler(request);
                if (outcome is Exception exception)
                {
                    return Task.FromException<T>(exception);
                }
                return Task.FromResult((T)outcome);
            }
        }

        if (typeof(T) == typeof(ResumeAnalysisOutput) &&
            request.UntrustedInput.Contains("analysis-mode: job_targeted", StringComparison.OrdinalIgnoreCase))
        {
            object strictJobAnalysis = new ResumeAnalysisOutput(
                ["Relevant C# experience"],
                ["Distributed systems exposure is limited"],
                ["Add a distributed systems project"],
                MatchScore: 78,
                Summary: "The profile has a grounded fit for the target role.",
                MatchedKeywordsOrSkills: ["C#", "PostgreSQL"],
                MissingKeywordsOrSkills: ["Distributed systems"],
                SectionFeedback: ["Experience is relevant and clearly presented."],
                Breakdown: new Dictionary<string, int>
                {
                    ["technicalSkillMatch"] = 80,
                    ["experienceRelevance"] = 78,
                    ["impactEvidence"] = 70,
                    ["clarity"] = 82,
                    ["structure"] = 80
                },
                Mode: ResumeAnalysisModes.JobTargeted);
            return Task.FromResult((T)strictJobAnalysis);
        }

        if (typeof(T) == typeof(ResumeAnalysisOutput) &&
            request.UntrustedInput.Contains("analysis-mode: field_benchmark", StringComparison.OrdinalIgnoreCase))
        {
            object strictFieldAnalysis = new ResumeAnalysisOutput(
                ["Strong C# foundation"],
                ["Limited evidence of architecture ownership"],
                ["Add a measurable architecture project"],
                ReadinessScore: 74,
                Summary: "The profile has a solid foundation for the target field.",
                SectionFeedback: ["Projects show relevant technical practice."],
                Breakdown: new Dictionary<string, int>
                {
                    ["technicalFoundation"] = 82,
                    ["projectEvidence"] = 72,
                    ["experiencePresentation"] = 70,
                    ["impactAchievements"] = 65,
                    ["clarity"] = 80,
                    ["roleAlignment"] = 76
                },
                Mode: ResumeAnalysisModes.FieldBenchmark);
            return Task.FromResult((T)strictFieldAnalysis);
        }

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
                    new StarComponentEvaluation(0, false, string.Empty, "Cần nêu kết quả cụ thể hơn."),
                    ["result"],
                    ["Có hành động xử lý rõ"],
                    ["Kết thúc câu trả lời bằng kết quả và tác động cụ thể."]) : new StarEvaluation(false, null, null, null, null, null, [], [], []),
                AiOperations.ScoreScale),
            var type when type == typeof(StarEvaluation) => new StarEvaluation(
                true,
                56,
                new StarComponentEvaluation(80, true, "Có bối cảnh tình huống.", "Bối cảnh rõ."),
                new StarComponentEvaluation(70, true, "Có nhiệm vụ cụ thể.", "Nhiệm vụ rõ."),
                new StarComponentEvaluation(75, true, "Có hành động xử lý.", "Hành động cá nhân rõ."),
                new StarComponentEvaluation(0, false, string.Empty, "Cần nêu kết quả cụ thể hơn."),
                ["result"],
                ["Có hành động xử lý rõ"],
                ["Kết thúc câu trả lời bằng kết quả và tác động cụ thể."],
                AiOperations.ScoreScale),
            var type when type == typeof(ScenarioEvaluationResult) => new ScenarioEvaluationResult(
                72,
                [
                    new ScenarioDimensionEvaluation("problem_analysis", 80, "Có phân tích nguyên nhân.", "Phân tích tốt."),
                    new ScenarioDimensionEvaluation("communication", 70, "Trình bày rõ ý.", "Cần cụ thể hơn.")
                ],
                ["Phân tích vấn đề rõ ràng"],
                ["Thiếu phương án dự phòng"],
                ["Đề xuất thêm phương án B"],
                "Nên bổ sung kết quả và phương án dự phòng.",
                AiOperations.ScoreScale),
            var type when type == typeof(InterviewReportOutput) => new InterviewReportOutput(
                [
                    new RubricScore("correctness", 75, "Transcript cho thấy hướng giải quyết phù hợp."),
                    new RubricScore("structure", 70, "Các ý có trình tự nhưng STAR chưa đầy đủ."),
                    new RubricScore("completeness", 65, "Transcript thiếu một số kết quả định lượng."),
                    new RubricScore("clarity", 80, "Câu trả lời rõ ràng và tập trung.")
                ],
                ["Diễn đạt rõ ràng"],
                ["Thiếu kết quả định lượng"],
                ["Luyện trả lời theo STAR"],
                AiOperations.ScoreScale),
            var type when type == typeof(ResumeAnalysisOutput) => new ResumeAnalysisOutput(
                ["Kinh nghiệm C# và PostgreSQL phù hợp yêu cầu", "Có kỹ năng phân tích hệ thống"],
                ["Cần bổ sung kiến thức về kiến trúc phân tán"],
                ["Tham gia thêm các bài test kỹ năng"]),
            var type when type == typeof(ResumeProfile) => new ResumeProfile(
                "Tóm tắt hồ sơ ứng viên",
                ["C#", "PostgreSQL"],
                [], [], [], [], []),
            _ => throw new InvalidOperationException($"Test provider does not support {typeof(T).Name}.")
        };
        return Task.FromResult((T)result);
    }
}
