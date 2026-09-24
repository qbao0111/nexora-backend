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

internal static class InterviewViewAssembler
{
    internal static InterviewView MapInterview(
        InterviewSession session,
        IEnumerable<InterviewQuestion> questions,
        IEnumerable<InterviewAnswer> answers,
        InterviewContinuationView? continuation = null,
        string reportState = InterviewReportStates.None,
        string resultState = InterviewResultStates.Collecting,
        InterviewEvaluationProgress? evaluationProgress = null,
        string questionPreparationState = InterviewQuestionPreparationStates.Processing) =>
        new(session.Id, session.Status, session.Role, session.Seniority, session.InterviewType, session.Difficulty, session.Version,
            questions.Where(item => item.ReleasedAt is not null &&
                (session.Status != PracticeValues.Active || item.Sequence <= InterviewQuestionValues.MaxQuestionsPerSession))
                .OrderBy(item => item.Sequence).Select(MapQuestion).ToArray(),
            answers.OrderBy(item => item.CreatedAt).Select(answer => MapAnswer(answer, session.Status)).ToArray(),
            session.CreatedAt, session.UpdatedAt, continuation, reportState, resultState,
            evaluationProgress ?? InterviewReadState.BuildEvaluationProgress(answers), questionPreparationState);

    internal static QuestionView MapQuestion(InterviewQuestion question) => new(
        question.Id,
        question.Sequence,
        question.Kind,
        question.Topic,
        question.ParentQuestionId,
        question.Content,
        question.CreatedAt);
    internal static AnswerView MapAnswer(InterviewAnswer answer, string interviewStatus = PracticeValues.Completed) => new(
        answer.Id,
        answer.QuestionId,
        answer.Content,
        answer.DurationSeconds,
        interviewStatus == PracticeValues.Active && answer.EvaluationStatus == InterviewAnswerEvaluationStates.Ready
            ? null
            : answer.EvaluationStatus == InterviewAnswerEvaluationStates.Ready
                ? ParseJson(answer.Evaluation)
                : null,
        answer.CreatedAt,
        answer.EvaluationStatus);
    internal static JsonElement? ParseJson(string? value) => value is null ? null : JsonSerializer.Deserialize<JsonElement>(value);

}
