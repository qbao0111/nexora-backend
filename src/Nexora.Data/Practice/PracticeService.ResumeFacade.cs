using Nexora.Business.Practice;

namespace Nexora.Data.Practice;

public sealed partial class PracticeService
{
    public Task<ResumeView> CreateResumeAsync(Guid userId, string uploadToken, CancellationToken cancellationToken) =>
        resumeService.CreateResumeAsync(userId, uploadToken, cancellationToken);

    public Task DeleteResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken) =>
        resumeService.DeleteResumeAsync(userId, resumeId, cancellationToken);

    public Task<ResumeView> GetResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken) =>
        resumeService.GetResumeAsync(userId, resumeId, cancellationToken);

    public Task<IReadOnlyList<ResumeView>> GetResumesAsync(Guid userId, CancellationToken cancellationToken) =>
        resumeService.GetResumesAsync(userId, cancellationToken);

    public Task<JobDescriptionView> CreateJobDescriptionAsync(Guid userId, string title, string content, CancellationToken cancellationToken, string? idempotencyKey = null) =>
        jobDescriptionService.CreateJobDescriptionAsync(userId, title, content, cancellationToken, idempotencyKey);

    public Task<IReadOnlyList<JobDescriptionView>> GetJobDescriptionsAsync(Guid userId, CancellationToken cancellationToken) =>
        jobDescriptionService.GetJobDescriptionsAsync(userId, cancellationToken);

    public Task<JobDescriptionView> GetJobDescriptionAsync(Guid userId, Guid jobDescriptionId, CancellationToken cancellationToken) =>
        jobDescriptionService.GetJobDescriptionAsync(userId, jobDescriptionId, cancellationToken);

    public Task<ResumeAnalysisView> StartResumeAnalysisAsync(Guid userId, StartResumeAnalysisCommand command, string idempotencyKey, CancellationToken cancellationToken) =>
        resumeAnalysisService.StartResumeAnalysisAsync(userId, command, idempotencyKey, cancellationToken);

    public Task<ResumeAnalysisView> GetResumeAnalysisAsync(Guid userId, Guid analysisId, CancellationToken cancellationToken) =>
        resumeAnalysisService.GetResumeAnalysisAsync(userId, analysisId, cancellationToken);

    public Task<ResumeAnalysisHistoryPage> GetResumeAnalysisHistoryAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken) =>
        resumeAnalysisService.GetResumeAnalysisHistoryAsync(userId, page, pageSize, cancellationToken);
}
