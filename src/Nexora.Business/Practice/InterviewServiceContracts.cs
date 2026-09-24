namespace Nexora.Business.Practice;

public interface IInterviewSessionService
{
    Task<InterviewView> StartInterviewAsync(Guid userId, StartInterviewCommand command, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewHistoryPage> GetInterviewHistoryAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken);
    Task<InterviewView> GetInterviewAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken);
    Task<InterviewView> PracticeAgainAsync(Guid userId, Guid interviewId, PracticeAgainCommand command, string idempotencyKey, CancellationToken cancellationToken);
}

public interface IInterviewFlowService
{
    Task<InterviewView> ContinueInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewView> RetryQuestionPreparationAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewView> CompleteInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewView> RetryReportAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewView> RetryResultsAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken);
}

public interface IInterviewAnswerService
{
    Task<AnswerResult> SubmitAnswerAsync(Guid userId, Guid interviewId, Guid questionId, string content, int? durationSeconds, string idempotencyKey, CancellationToken cancellationToken);
}

public interface IInterviewReportService
{
    Task<ReportView> GetReportAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken);
}

public interface IPracticeDashboardService
{
    Task<DashboardView> GetDashboardAsync(Guid userId, CancellationToken cancellationToken);
}
