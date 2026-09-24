using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

using static Nexora.Data.Practice.InterviewErrors;
using static Nexora.Data.Practice.InterviewQuestionContracts;
using static Nexora.Data.Practice.InterviewViewAssembler;

namespace Nexora.Data.Practice;

public sealed class InterviewReportService(NexoraDbContext dbContext) : IInterviewReportService
{
    public async Task<ReportView> GetReportAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken)
    {
        var report = await dbContext.InterviewReports.AsNoTracking()
            .Include(item => item.InterviewSession).ThenInclude(item => item.Answers)
            .Include(item => item.InterviewSession).ThenInclude(item => item.Questions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.InterviewSessionId == interviewId && item.UserId == userId, cancellationToken);
        if (report is not null)
            return MapReport(report, report.InterviewSession.Questions, report.InterviewSession.Answers);

        var session = await dbContext.InterviewSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        if (session.Status == PracticeValues.Completing)
        {
            var latestJob = (await dbContext.OutboxEvents.AsNoTracking()
                    .Where(item => item.AggregateId == interviewId && item.Type == "InterviewReportRequested")
                    .ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
            if (latestJob?.Status == BillingValues.Failed)
                throw Conflict("INTERVIEW_REPORT_FAILED", "Báo cáo phỏng vấn chưa tạo được. Bạn có thể thử lại.");

            throw Conflict("INTERVIEW_REPORT_PROCESSING", "Báo cáo phỏng vấn đang được xử lý.");
        }

        throw Conflict("INTERVIEW_REPORT_UNAVAILABLE", "Báo cáo phỏng vấn chưa sẵn sàng.");
    }


    private static int WeightedStarScore(StarComponentEvaluation situation, StarComponentEvaluation task, StarComponentEvaluation action, StarComponentEvaluation result) =>
        (int)Math.Round(situation.Score * .20 + task.Score * .20 + action.Score * .35 + result.Score * .25);

    private static StarReportSummary? BuildStarSummary(
        IEnumerable<InterviewQuestion> questions,
        IEnumerable<InterviewAnswer> answers)
    {
        var questionMap = questions.ToDictionary(item => item.Id);
        ValidateQuestionContracts(questionMap.Values);
        var evaluations = new List<PersistedStarEvaluation>();
        foreach (var answer in answers)
        {
            if (TryReadStarEvaluation(answer, questionMap, out var evaluation) && evaluation is not null)
                evaluations.Add(evaluation);
        }

        var stories = evaluations
            .GroupBy(item => item.RootQuestionId)
            .Select(story => BuildStarStory(story, questionMap))
            .OrderByDescending(item => item.Evaluations.Count)
            .ThenBy(item => item.RootSequence)
            .ToArray();
        if (stories.Length == 0) return null;

        var story = stories[0];
        var orderedEvaluations = story.Evaluations.ToArray();

        var situation = MergeStarComponent(orderedEvaluations, star => star.Situation);
        var task = MergeStarComponent(orderedEvaluations, star => star.Task);
        var action = MergeStarComponent(orderedEvaluations, star => star.Action);
        var result = MergeStarComponent(orderedEvaluations, star => star.Result);
        var components = new[]
        {
            (Name: "situation", Component: situation),
            (Name: "task", Component: task),
            (Name: "action", Component: action),
            (Name: "result", Component: result)
        };

        var strongest = components
            .Select((item, index) => (item, index))
            .OrderByDescending(item => item.item.Component.Score)
            .ThenBy(item => item.index)
            .First().item.Name;
        var weakest = components
            .Select((item, index) => (item, index))
            .OrderBy(item => item.item.Component.Score)
            .ThenBy(item => item.index)
            .First().item.Name;

        return new StarReportSummary(
            orderedEvaluations.Length,
            WeightedStarScore(situation, task, action, result),
            new StarComponentAverages(situation.Score, task.Score, action.Score, result.Score),
            strongest,
            weakest,
            components
                .Where(item => !item.Component.Detected || item.Component.Score < 60)
                .Select(item => item.Name)
                .Take(3)
                .ToArray(),
            components
                .Select((item, index) => (item, index))
                .Where(item => !item.item.Component.Detected || item.item.Component.Score < 60)
                .OrderBy(item => item.item.Component.Score)
                .ThenBy(item => item.index)
                .Select(item => item.item.Component.Feedback)
                .Where(NotBlank)
                .Select(item => item!.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToArray());
    }

    private static bool TryReadStarEvaluation(
        InterviewAnswer answer,
        Dictionary<Guid, InterviewQuestion> questions,
        out PersistedStarEvaluation? evaluation)
    {
        evaluation = null;
        if (string.IsNullOrWhiteSpace(answer.Evaluation) || !questions.TryGetValue(answer.QuestionId, out var question))
            return false;

        try
        {
            var star = JsonSerializer.Deserialize<AnswerEvaluation>(answer.Evaluation, JsonOptions)?.Star;
            if (!IsUsableStar(star)) return false;
            evaluation = new PersistedStarEvaluation(
                answer.Id,
                question.Sequence,
                ResolveStoryRoot(question.Id, questions),
                answer.CreatedAt,
                star!);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsUsableStar(StarEvaluation? star) =>
        star is not null && star.Applicable &&
        IsUsableStarComponent(star.Situation) &&
        IsUsableStarComponent(star.Task) &&
        IsUsableStarComponent(star.Action) &&
        IsUsableStarComponent(star.Result);

    private static bool IsUsableStarComponent(StarComponentEvaluation? component)
    {
        if (component is null || component.Score is < 0 or > 100) return false;
        return component.Detected
            ? component.Score > 0 && !string.IsNullOrWhiteSpace(component.Evidence)
            : component.Score == 0 && string.IsNullOrWhiteSpace(component.Evidence);
    }

    private static StarComponentEvaluation MergeStarComponent(
        IReadOnlyCollection<PersistedStarEvaluation> evaluations,
        Func<StarEvaluation, StarComponentEvaluation?> selector)
    {
        var candidates = evaluations
            .Select(item => new { item, component = selector(item.Star) })
            .Where(item => item.component is not null)
            .ToArray();
        var strongestDetected = candidates
            .Where(item => item.component!.Detected && !string.IsNullOrWhiteSpace(item.component.Evidence))
            .OrderByDescending(item => item.component!.Score)
            .ThenByDescending(item => item.item.Sequence)
            .ThenByDescending(item => item.item.CreatedAt)
            .ThenByDescending(item => item.item.AnswerId)
            .Select(item => item.component!)
            .FirstOrDefault();
        if (strongestDetected is not null) return strongestDetected;

        var latestUndetected = candidates
            .OrderByDescending(item => item.item.Sequence)
            .ThenByDescending(item => item.item.CreatedAt)
            .ThenByDescending(item => item.item.AnswerId)
            .Select(item => item.component!)
            .FirstOrDefault();
        return latestUndetected is null
            ? new StarComponentEvaluation(0, false, string.Empty, string.Empty)
            : new StarComponentEvaluation(0, false, string.Empty, latestUndetected.Feedback);
    }

    private static StarStorySummary BuildStarStory(
        IGrouping<Guid, PersistedStarEvaluation> story,
        Dictionary<Guid, InterviewQuestion> questions)
    {
        var evaluations = story
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.AnswerId)
            .ToArray();
        if (!questions.TryGetValue(story.Key, out var rootQuestion) ||
            !string.Equals(rootQuestion.Kind, InterviewQuestionValues.Primary, StringComparison.Ordinal))
            throw InvalidState();
        return new StarStorySummary(story.Key, rootQuestion.Sequence, evaluations);
    }

    private static Guid ResolveStoryRoot(Guid questionId, Dictionary<Guid, InterviewQuestion> questions)
    {
        var visited = new HashSet<Guid>();
        var currentId = questionId;
        while (true)
        {
            if (!visited.Add(currentId) || !questions.TryGetValue(currentId, out var question))
                throw InvalidState();
            if (question.ParentQuestionId is null) return question.Id;
            currentId = question.ParentQuestionId.Value;
        }
    }

    internal static ReportView MapReport(InterviewReport report, IEnumerable<InterviewQuestion> questions, IEnumerable<InterviewAnswer> answers)
    {
        var orderedQuestions = questions.Where(item => item.ReleasedAt is not null).OrderBy(item => item.Sequence).ToArray();
        var questionReviews = BuildQuestionReviews(orderedQuestions, answers);
        var suggestions = questionReviews
            .Where(item => !string.IsNullOrWhiteSpace(item.SuggestedImprovedAnswer))
            .Select(item => new SuggestedImprovedAnswerView(item.QuestionId, item.Sequence, item.SuggestedImprovedAnswer!))
            .ToArray();
        var sample = new ReportSampleView(
            questionReviews.Length,
            orderedQuestions.Length,
            IsPartialReport(questionReviews.Length, orderedQuestions.Length));
        return new ReportView(
            report.Id,
            report.InterviewSessionId,
            report.OverallScore,
            ParseJson(report.Rubric) ?? default(JsonElement),
            ParseJson(report.Strengths) ?? default(JsonElement),
            ParseJson(report.Gaps) ?? default(JsonElement),
            ParseJson(report.ActionPlan) ?? default(JsonElement),
            report.Disclaimer,
            report.CreatedAt,
            BuildStarSummary(orderedQuestions, answers),
            questionReviews,
            suggestions,
            sample);
    }

    private static InterviewQuestionReviewView[] BuildQuestionReviews(
        IReadOnlyCollection<InterviewQuestion> questions,
        IEnumerable<InterviewAnswer> answers)
    {
        var answersByQuestion = answers
            .Where(item => !string.IsNullOrWhiteSpace(item.Content))
            .GroupBy(item => item.QuestionId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.CreatedAt).First());
        return questions
            .Where(item => answersByQuestion.ContainsKey(item.Id))
            .Select(question =>
            {
                var answer = answersByQuestion[question.Id];
                var evaluation = TryDeserializeAnswerEvaluation(answer.Evaluation);
                return new InterviewQuestionReviewView(
                    question.Id,
                    question.Sequence,
                    question.Kind,
                    question.Topic,
                    question.ParentQuestionId,
                    question.Content,
                    answer.Content,
                    evaluation?.Scores ?? [],
                    evaluation?.Feedback ?? string.Empty,
                    evaluation?.Star,
                    evaluation?.Strengths ?? [],
                    evaluation?.Improvements ?? [],
                    evaluation?.ImprovedAnswer,
                    evaluation?.SampleAnswer);
            })
            .ToArray();
    }

    internal static bool IsPartialReport(int answeredQuestions, int issuedQuestions) =>
        answeredQuestions < issuedQuestions || answeredQuestions <= InterviewQuestionValues.FreeQuestionLimit;

    private sealed record PersistedStarEvaluation(
        Guid AnswerId,
        int Sequence,
        Guid RootQuestionId,
        DateTimeOffset CreatedAt,
        StarEvaluation Star);

    private sealed record StarStorySummary(
        Guid RootQuestionId,
        int RootSequence,
        IReadOnlyCollection<PersistedStarEvaluation> Evaluations);


}
