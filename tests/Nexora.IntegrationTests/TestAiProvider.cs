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
    private readonly ConcurrentDictionary<string, Queue<Func<AiRequest, CancellationToken, Task<object>>>> _scriptedResponses = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<AiRequest> Invocations => _invocations.ToArray();

    public int GetCallCount(string purpose) => _callCounts.TryGetValue(purpose, out var count) ? count : 0;
    public int TotalCalls => _invocations.Count;

    public void EnqueueResponse(string purpose, object responseOrException)
    {
        var queue = _scriptedResponses.GetOrAdd(purpose, _ => new Queue<Func<AiRequest, CancellationToken, Task<object>>>());
        lock (queue)
        {
            queue.Enqueue((_, _) => Task.FromResult(responseOrException));
        }
    }

    public void EnqueueHandler(string purpose, Func<AiRequest, object> handler)
    {
        var queue = _scriptedResponses.GetOrAdd(purpose, _ => new Queue<Func<AiRequest, CancellationToken, Task<object>>>());
        lock (queue)
        {
            queue.Enqueue((request, _) => Task.FromResult(handler(request)));
        }
    }

    public void EnqueueAsyncHandler(string purpose, Func<AiRequest, CancellationToken, Task<object>> handler)
    {
        var queue = _scriptedResponses.GetOrAdd(purpose, _ => new Queue<Func<AiRequest, CancellationToken, Task<object>>>());
        lock (queue) queue.Enqueue(handler);
    }

    public void Reset()
    {
        _invocations.Clear();
        _callCounts.Clear();
        _scriptedResponses.Clear();
    }

    public async Task<T> GenerateStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _invocations.Enqueue(request);
        _callCounts.AddOrUpdate(request.Purpose, 1, (_, current) => current + 1);

        if (_scriptedResponses.TryGetValue(request.Purpose, out var queue))
        {
            Func<AiRequest, CancellationToken, Task<object>>? handler = null;
            lock (queue)
            {
                if (queue.Count > 0)
                {
                    handler = queue.Dequeue();
                }
            }

            if (handler is not null)
            {
                var outcome = await handler(request, cancellationToken);
                if (outcome is Exception exception)
                {
                    throw exception;
                }
                return (T)outcome;
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
            return (T)strictJobAnalysis;
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
            return (T)strictFieldAnalysis;
        }

        var questionTopic = GetQuestionTopic(request.UntrustedInput);
        var behavioral = string.Equals(questionTopic, InterviewQuestionValues.BehavioralStar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(questionTopic, InterviewQuestionValues.Behavioral, StringComparison.OrdinalIgnoreCase)
            || (questionTopic is null && request.UntrustedInput.Contains("interview-type: behavioral", StringComparison.OrdinalIgnoreCase));
        var generatedQuestion = request.Purpose == "interview.followup"
            ? "Bạn sẽ cải thiện kết quả đó như thế nào nếu làm lại?"
            : questionTopic?.ToLowerInvariant() switch
            {
                InterviewQuestionValues.SelfIntroduction => "Hãy giới thiệu ngắn gọn về kinh nghiệm phù hợp nhất với vai trò này.",
                InterviewQuestionValues.BehavioralStar => "Hãy kể về một tình huống bạn giải quyết vấn đề khó trong vai trò này.",
                InterviewQuestionValues.MotivationRoleFit => "Điều gì thu hút bạn ở vai trò này và vì sao bạn phù hợp?",
                InterviewQuestionValues.Behavioral => "Hãy kể về một tình huống bạn giải quyết vấn đề khó trong vai trò này.",
                _ when behavioral => "Hãy kể về một tình huống bạn giải quyết vấn đề khó trong vai trò này.",
                _ => "Explain dependency injection."
            };
        var sequence = GetLabeledValue(request.UntrustedInput, "question-sequence:");
        generatedQuestion = sequence switch
        {
            "3" => "Hãy đưa một ví dụ cụ thể cho năng lực này và giải thích kết quả đạt được.",
            "4" => "Bạn sẽ chọn cách xử lý nào khi gặp một ràng buộc mới trong công việc?",
            "5" => "Bạn cân nhắc những đánh đổi nào trước khi đưa ra quyết định cuối cùng?",
            _ => generatedQuestion
        };
        if (questionTopic is not null)
            generatedQuestion = $"[{questionTopic}] {generatedQuestion}";
        var candidateAnswer = GetLabeledValue(request.UntrustedInput, "answer:");
        var groundedStrength = string.IsNullOrWhiteSpace(candidateAnswer)
            ? "Clear response."
            : $"Grounded point: {candidateAnswer[..Math.Min(candidateAnswer.Length, 120)]}";
        var groundedEvidence = string.IsNullOrWhiteSpace(candidateAnswer)
            ? "Candidate answer."
            : candidateAnswer[..Math.Min(candidateAnswer.Length, 120)];
        var reportAnswer = GetFirstTranscriptAnswer(request.UntrustedInput) ?? "answer";
        var reportEvidence = reportAnswer[..Math.Min(reportAnswer.Length, 120)];
        var improvedAnswer = string.IsNullOrWhiteSpace(candidateAnswer)
            ? "Keep the same answer and add concrete evidence if available."
            : candidateAnswer;
        object result = typeof(T) switch
        {
            var type when type == typeof(GeneratedQuestion) => new GeneratedQuestion(generatedQuestion),
            var type when type == typeof(AnswerEvaluation) => new AnswerEvaluation(
                [
                    new RubricScore("correctness", 75, groundedEvidence),
                    new RubricScore("structure", 70, groundedEvidence),
                    new RubricScore("completeness", 65, groundedEvidence),
                    new RubricScore("clarity", 80, groundedEvidence)
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
                AiOperations.ScoreScale,
                Strengths: [groundedStrength],
                Improvements: ["Bổ sung một kết quả cụ thể nếu có."],
                ImprovedAnswer: improvedAnswer),
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
                    new RubricScore("correctness", 75, $"Evidence from answer: {reportEvidence}"),
                    new RubricScore("structure", 70, $"Evidence from answer: {reportEvidence}"),
                    new RubricScore("completeness", 65, $"Evidence from answer: {reportEvidence}"),
                    new RubricScore("clarity", 80, $"Evidence from answer: {reportEvidence}")
                ],
                [$"Grounded point: {reportEvidence}"],
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
        return (T)result;
    }

    private static string? GetQuestionTopic(string input) => input
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => line.StartsWith("question-topic:", StringComparison.OrdinalIgnoreCase))
        .Select(line => line["question-topic:".Length..].Trim())
        .Select(topic => string.IsNullOrWhiteSpace(topic) ? null : topic)
        .FirstOrDefault();

    private static string? GetLabeledValue(string input, string label) => input
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
        .Select(line => line[label.Length..].Trim())
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? GetFirstTranscriptAnswer(string input) => input
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => line.StartsWith("A:", StringComparison.OrdinalIgnoreCase))
        .Select(line => line[2..].Trim())
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
