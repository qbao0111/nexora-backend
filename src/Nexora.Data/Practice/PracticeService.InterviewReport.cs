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

namespace Nexora.Data.Practice;

public sealed partial class PracticeService
{
    private async Task BuildReportAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.AsNoTracking().Include(item => item.Questions).ThenInclude(question => question.Answer)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (await dbContext.InterviewReports.AsNoTracking().AnyAsync(item => item.InterviewSessionId == snapshot.Id, cancellationToken))
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }
        if (snapshot.Status == PracticeValues.Completed)
            throw InvalidState();

        var answeredQuestions = snapshot.Questions
            .Where(item => item.Answer is not null && !string.IsNullOrWhiteSpace(item.Answer.Content))
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (answeredQuestions.Length < MinimumReportAnswers)
            throw InvalidState();
        var transcript = string.Join("\n", answeredQuestions
            .Select(item => $"Q: {item.Content}\nA: {item.Answer!.Content}"));
        // Reports must be grounded in the observed interview answers. Resume
        // profile claims are not interview evidence and are intentionally not
        // included in the synthesis input.
        var reportContext = resumeContextBuilder.BuildReportContext(transcript, profile: null);
        var groundingTranscript = string.Join("\n", answeredQuestions.Select(item => item.Answer!.Content));
        var reportOperationContext = new AiOperationContext(
            snapshot.Id.ToString("N"),
            snapshot.UserId,
            GroundingTranscript: groundingTranscript);
        AiExecutionResult<InterviewReportOutput> execResult;
        var fallbackUsed = false;
        try
        {
            execResult = await structuredAiExecutor.ExecuteAsync(
                AiOperations.InterviewReport,
                reportContext,
                reportOperationContext,
                cancellationToken);
        }
        catch (BusinessException ex) when (string.Equals(ex.Code, "AI_OUTPUT_INVALID", StringComparison.Ordinal))
        {
            var fallback = BuildDeterministicReportFallback(answeredQuestions, reportOperationContext);
            if (fallback is null)
                throw;
            execResult = fallback;
            fallbackUsed = true;
        }
        var output = execResult.Value;
        ValidateScores(output.Scores);
        if (output.Gaps.Count == 0 || output.ActionPlan.Count == 0) throw InvalidAiOutput();
        var overall = WeightedScore(output.Scores);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(snapshot.UserId, job.AggregateId, cancellationToken) ?? throw NotFound();
        var existing = await dbContext.InterviewReports.SingleOrDefaultAsync(item => item.InterviewSessionId == session.Id, cancellationToken);
        if (existing is not null)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return;
        }
        if (session.Status != PracticeValues.Completing) throw InvalidState();
        var now = timeProvider.GetUtcNow();
        dbContext.InterviewReports.Add(new InterviewReport
        {
            Id = Guid.NewGuid(),
            UserId = session.UserId,
            InterviewSessionId = session.Id,
            OverallScore = overall,
            Rubric = JsonSerializer.Serialize(output.Scores, JsonOptions),
            Strengths = JsonSerializer.Serialize(output.Strengths, JsonOptions),
            Gaps = JsonSerializer.Serialize(output.Gaps, JsonOptions),
            ActionPlan = JsonSerializer.Serialize(output.ActionPlan, JsonOptions),
            Disclaimer = BuildReportDisclaimer(answeredQuestions.Length, snapshot.Questions.Count),
            ModelVersion = execResult.ModelVersion,
            PromptVersion = execResult.PromptVersion,
            RubricVersion = execResult.RubricVersion,
            SchemaVersion = execResult.SchemaVersion,
            CreatedAt = now
        });
        session.Status = PracticeValues.Completed;
        EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
        session.Version++;
        session.UpdatedAt = now;
        session.CompletedAt = now;
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        if (fallbackUsed)
            ReportFallbackPersisted(logger, AiOperations.InterviewReport.Purpose, aiProvider.ModelVersion, session.Id, reportOperationContext.CorrelationId);
    }

    private static void ValidateScores(IReadOnlyCollection<RubricScore> scores)
    {
        var required = new[] { "correctness", "structure", "completeness", "clarity" };
        if (scores.Count != required.Length || required.Any(name => scores.Count(item => item.Criterion == name) != 1) ||
            scores.Any(item => item.Score is < 0 or > 100 || string.IsNullOrWhiteSpace(item.Evidence))) throw InvalidAiOutput();
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

    private static AiExecutionResult<InterviewReportOutput>? BuildDeterministicReportFallback(
        InterviewQuestion[] answeredQuestions,
        AiOperationContext context)
    {
        if (answeredQuestions.Any(question => question.Answer?.EvaluationStatus != InterviewAnswerEvaluationStates.Ready))
            return null;

        var evaluations = answeredQuestions
            .Select(question => new
            {
                Question = question,
                Answer = question.Answer!,
                Evaluation = TryDeserializeAnswerEvaluation(question.Answer!.Evaluation)
            })
            .Where(item => item.Evaluation is not null)
            .ToArray();
        if (evaluations.Length != answeredQuestions.Length)
            return null;

        var scores = new List<RubricScore>(CanonicalRubricValidator.RequiredCriteria.Length);
        foreach (var criterion in CanonicalRubricValidator.RequiredCriteria)
        {
            var candidates = evaluations
                .Select(item => new
                {
                    item.Question.Sequence,
                    item.Answer.Id,
                    Score = item.Evaluation!.Scores.SingleOrDefault(score =>
                        string.Equals(score.Criterion, criterion, StringComparison.Ordinal))
                })
                .ToArray();
            if (candidates.Any(item => item.Score is null))
                return null;

            var aggregate = (int)Math.Round(
                candidates.Average(item => item.Score!.Score),
                MidpointRounding.AwayFromZero);
            var evidence = candidates
                .OrderBy(item => Math.Abs(item.Score!.Score - aggregate))
                .ThenBy(item => item.Sequence)
                .ThenBy(item => item.Id)
                .Select(item => item.Score!.Evidence.Trim())
                .FirstOrDefault(item => AnswerCoachingValidator.IsGroundedReportEvidence(item, context.GroundingTranscript!));
            evidence ??= evaluations
                .OrderBy(item => item.Question.Sequence)
                .ThenBy(item => item.Answer.Id)
                .Select(item => item.Answer.Content.Trim()[..Math.Min(item.Answer.Content.Trim().Length, 500)])
                .FirstOrDefault(item => AnswerCoachingValidator.IsGroundedReportEvidence(item, context.GroundingTranscript!));
            if (string.IsNullOrWhiteSpace(evidence))
                return null;
            scores.Add(new RubricScore(criterion, aggregate, evidence));
        }

        var strengths = evaluations
            .OrderBy(item => item.Question.Sequence)
            .ThenBy(item => item.Answer.Id)
            .SelectMany(item => item.Evaluation!.Strengths ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => item.Length is > 0 and <= 500 &&
                AnswerCoachingValidator.IsGroundedReportStrength(item, context.GroundingTranscript!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        var improvements = evaluations
            .OrderBy(item => item.Question.Sequence)
            .ThenBy(item => item.Answer.Id)
            .SelectMany(item => item.Evaluation!.Improvements ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => item.Length is > 0 and <= 500)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        var weakestCriterion = scores.OrderBy(item => item.Score).First().Criterion;
        var gap = weakestCriterion switch
        {
            "structure" => "Cần trình bày câu trả lời theo cấu trúc rõ ràng hơn.",
            "completeness" => "Cần đề cập đầy đủ các ý của câu hỏi.",
            "clarity" => "Cần diễn đạt lập luận rõ và ít mơ hồ hơn.",
            _ => "Cần kiểm tra giả định và giải thích cơ sở của câu trả lời."
        };
        var action = weakestCriterion switch
        {
            "structure" => "Hãy sắp xếp câu trả lời theo mở đầu, lập luận và kết luận.",
            "completeness" => "Hãy đối chiếu từng phần của câu hỏi và bổ sung phần còn thiếu.",
            "clarity" => "Hãy nêu trực tiếp ý chính và giải thích từng bước lập luận.",
            _ => "Hãy nêu rõ giả định và kiểm tra cơ sở kỹ thuật hoặc nghiệp vụ."
        };

        var fallback = new InterviewReportOutput(
            scores,
            strengths,
            improvements.Length > 0 ? improvements : [gap],
            improvements.Length > 0 ? improvements : [action],
            AiOperations.ScoreScale);
        var validation = AiOperations.InterviewReport.NormalizeAndValidate(fallback, context);
        if (!validation.IsValid)
            return null;

        return new AiExecutionResult<InterviewReportOutput>(
            validation.NormalizedValue!,
            "deterministic:validated-answer-aggregate-v2",
            "interview-report-fallback-v2",
            AiOperations.InterviewReport.SchemaVersion,
            AiOperations.InterviewReport.RubricVersion,
            true,
            AiOperations.InterviewReport.MaxAttempts,
            0);
    }

    private static int WeightedScore(IReadOnlyCollection<RubricScore> scores)
    {
        var values = scores.ToDictionary(item => item.Criterion, item => item.Score, StringComparer.Ordinal);
        return (int)Math.Round(values["correctness"] * .40 + values["structure"] * .25 + values["completeness"] * .20 + values["clarity"] * .15);
    }

    private AiRequest Request(string purpose, string input, Guid correlationId)
    {
        var schema = purpose switch
        {
            "resume.profile" => ProfileSchema,
            "resume.analysis" => AnalysisSchema,
            "interview.evaluate" => EvaluationSchema,
            "interview.report" => ReportSchema,
            "interview.first-question" or "interview.followup" => QuestionSchema,
            _ => EmptySchema
        };
        var isProfile = purpose == "resume.profile";
        var maxOutputTokens = purpose == "interview.report" ? 4_000 : isProfile ? 3_000 : 2_000;
        return new(purpose,
            isProfile ? ProfilePromptVersion : PromptVersion,
            CurrentModelVersion,
            RubricVersion,
            isProfile ? ProfileSchemaVersion : SchemaVersion,
            Bound(input), schema, maxOutputTokens, correlationId.ToString("N"));
    }

    private static ReportView MapReport(InterviewReport report, IEnumerable<InterviewQuestion> questions, IEnumerable<InterviewAnswer> answers)
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

    private static bool IsPartialReport(int answeredQuestions, int issuedQuestions) =>
        answeredQuestions < issuedQuestions || answeredQuestions <= InterviewQuestionValues.FreeQuestionLimit;

    private static string BuildReportDisclaimer(int answeredQuestions, int issuedQuestions) =>
        IsPartialReport(answeredQuestions, issuedQuestions)
            ? $"{Disclaimer} This is a partial sample based on {answeredQuestions} of {issuedQuestions} answered questions; it is not a full competency assessment."
            : $"{Disclaimer} This coaching report is based on {answeredQuestions} answered questions.";
}
