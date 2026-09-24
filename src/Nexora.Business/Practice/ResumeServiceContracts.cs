namespace Nexora.Business.Practice;

public interface IResumeService
{
    Task<ResumeView> CreateResumeAsync(Guid userId, string uploadToken, CancellationToken cancellationToken);
    Task DeleteResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken);
    Task<ResumeView> GetResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ResumeView>> GetResumesAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IJobDescriptionService
{
    Task<JobDescriptionView> CreateJobDescriptionAsync(Guid userId, string title, string content, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<IReadOnlyList<JobDescriptionView>> GetJobDescriptionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<JobDescriptionView> GetJobDescriptionAsync(Guid userId, Guid jobDescriptionId, CancellationToken cancellationToken);
}

public interface IResumeAnalysisService
{
    Task<ResumeAnalysisView> StartResumeAnalysisAsync(Guid userId, StartResumeAnalysisCommand command, string idempotencyKey, CancellationToken cancellationToken);
    Task<ResumeAnalysisView> GetResumeAnalysisAsync(Guid userId, Guid analysisId, CancellationToken cancellationToken);
    Task<ResumeAnalysisHistoryPage> GetResumeAnalysisHistoryAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken);
}
