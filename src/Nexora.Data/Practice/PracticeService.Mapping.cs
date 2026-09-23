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
    private static ResumeView MapResume(ResumeRecord resume) => new(
        resume.Id,
        resume.StoredFile.FileName,
        resume.StoredFile.ContentType,
        resume.StoredFile.Size,
        resume.Status,
        resume.CreatedAt,
        resume.Status == PracticeValues.Failed ? "RESUME_EXTRACTION_FAILED" : null,
        resume.Status == PracticeValues.Failed ? ResumeExtractionFailureMessage : null);
    private static JobDescriptionView MapJobDescription(JobDescription jd) => new(jd.Id, jd.Title, jd.Content, jd.CreatedAt);
    private static ResumeAnalysisView MapAnalysis(ResumeAnalysis analysis) => new(
        analysis.Id,
        analysis.Status,
        ParseJson(analysis.Result),
        analysis.CreatedAt,
        analysis.CompletedAt,
        analysis.ErrorCode,
        analysis.Mode,
        ParseAnalysisContext(analysis.ContextJson, analysis.Mode),
        analysis.ResumeVersion,
        analysis.JobDescriptionVersion,
        analysis.ModelVersion,
        analysis.PromptVersion,
        analysis.SchemaVersion,
        analysis.RubricVersion,
        analysis.ProfileModelVersion,
        analysis.ProfilePromptVersion,
        analysis.ProfileSchemaVersion);
    private static InterviewView MapInterview(
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
            evaluationProgress ?? BuildEvaluationProgress(answers), questionPreparationState);

    private static QuestionView MapQuestion(InterviewQuestion question) => new(
        question.Id,
        question.Sequence,
        question.Kind,
        question.Topic,
        question.ParentQuestionId,
        question.Content,
        question.CreatedAt);
    private static AnswerView MapAnswer(InterviewAnswer answer, string interviewStatus = PracticeValues.Completed) => new(
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
    private static JsonElement? ParseJson(string? value) => value is null ? null : JsonSerializer.Deserialize<JsonElement>(value);
    private static AnswerEvaluation? TryDeserializeAnswerEvaluation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<AnswerEvaluation>(value, JsonOptions); }
        catch (JsonException) { return null; }
    }

}
