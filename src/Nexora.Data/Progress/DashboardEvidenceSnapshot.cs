using Microsoft.EntityFrameworkCore;
using Nexora.Business.Practice;
using Nexora.Data.Persistence;

namespace Nexora.Data.Progress;

internal sealed record DashboardReportRow(Guid Id, Guid InterviewSessionId, int OverallScore, string Rubric, DateTimeOffset CreatedAt);
internal sealed record DashboardAnswerRow(Guid Id, Guid InterviewSessionId, string Evaluation, DateTimeOffset CreatedAt);
internal sealed record DashboardScenarioRow(Guid Id, string? Competency, string? EvaluationJson, DateTimeOffset? CompletedAt, DateTimeOffset UpdatedAt);
internal sealed record DashboardStarRow(Guid Id, string? EvaluationJson, DateTimeOffset? CompletedAt, DateTimeOffset UpdatedAt);

internal sealed record DashboardEvidenceSnapshot(
    DashboardReportRow[] Reports,
    DashboardAnswerRow[] Answers,
    DashboardScenarioRow[] Scenarios,
    DashboardStarRow[] Stars)
{
    internal static async Task<DashboardEvidenceSnapshot> LoadAsync(NexoraDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var reports = await dbContext.InterviewReports.AsNoTracking()
            .Where(item => item.UserId == userId && item.InterviewSession.UserId == userId)
            .Select(item => new DashboardReportRow(item.Id, item.InterviewSessionId, item.OverallScore, item.Rubric, item.CreatedAt))
            .ToArrayAsync(cancellationToken);
        var answers = await dbContext.InterviewAnswers.AsNoTracking()
            .Where(item => item.UserId == userId && item.InterviewSession.UserId == userId &&
                           item.EvaluationStatus == InterviewAnswerEvaluationStates.Ready && item.Evaluation != null)
            .Select(item => new DashboardAnswerRow(item.Id, item.InterviewSessionId, item.Evaluation!, item.CreatedAt))
            .ToArrayAsync(cancellationToken);
        var scenarios = await dbContext.ScenarioAttempts.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed)
            .Select(item => new DashboardScenarioRow(item.Id, item.Scenario.Competency, item.EvaluationJson, item.CompletedAt, item.UpdatedAt))
            .ToArrayAsync(cancellationToken);
        var stars = await dbContext.StarAttempts.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed)
            .Select(item => new DashboardStarRow(item.Id, item.EvaluationJson, item.CompletedAt, item.UpdatedAt))
            .ToArrayAsync(cancellationToken);
        return new DashboardEvidenceSnapshot(reports, answers, scenarios, stars);
    }
}
