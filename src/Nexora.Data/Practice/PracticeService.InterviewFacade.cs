using Nexora.Business.Practice;

namespace Nexora.Data.Practice;

public sealed partial class PracticeService
{
    public Task<InterviewView> StartInterviewAsync(Guid userId, StartInterviewCommand command, string idempotencyKey, CancellationToken cancellationToken) =>
        interviewSessionService.StartInterviewAsync(userId, command, idempotencyKey, cancellationToken);

    public Task<InterviewHistoryPage> GetInterviewHistoryAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken) =>
        interviewSessionService.GetInterviewHistoryAsync(userId, page, pageSize, cancellationToken);

    public Task<InterviewView> GetInterviewAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken) =>
        interviewSessionService.GetInterviewAsync(userId, interviewId, cancellationToken);

    public Task<InterviewView> PracticeAgainAsync(Guid userId, Guid interviewId, PracticeAgainCommand command, string idempotencyKey, CancellationToken cancellationToken) =>
        interviewSessionService.PracticeAgainAsync(userId, interviewId, command, idempotencyKey, cancellationToken);

    public Task<AnswerResult> SubmitAnswerAsync(Guid userId, Guid interviewId, Guid questionId, string content, int? durationSeconds, string idempotencyKey, CancellationToken cancellationToken) =>
        interviewAnswerService.SubmitAnswerAsync(userId, interviewId, questionId, content, durationSeconds, idempotencyKey, cancellationToken);

    public Task<InterviewView> ContinueInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken) =>
        interviewFlowService.ContinueInterviewAsync(userId, interviewId, idempotencyKey, cancellationToken);

    public Task<InterviewView> RetryQuestionPreparationAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken) =>
        interviewFlowService.RetryQuestionPreparationAsync(userId, interviewId, idempotencyKey, cancellationToken);

    public Task<InterviewView> CompleteInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken) =>
        interviewFlowService.CompleteInterviewAsync(userId, interviewId, idempotencyKey, cancellationToken);

    public Task<InterviewView> RetryReportAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken) =>
        interviewFlowService.RetryReportAsync(userId, interviewId, idempotencyKey, cancellationToken);

    public Task<InterviewView> RetryResultsAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken) =>
        interviewFlowService.RetryResultsAsync(userId, interviewId, idempotencyKey, cancellationToken);

    public Task<ReportView> GetReportAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken) =>
        interviewReportService.GetReportAsync(userId, interviewId, cancellationToken);

    public Task<DashboardView> GetDashboardAsync(Guid userId, CancellationToken cancellationToken) =>
        practiceDashboardService.GetDashboardAsync(userId, cancellationToken);
}
