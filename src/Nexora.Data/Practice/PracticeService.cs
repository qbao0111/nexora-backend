using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class PracticeService(
    NexoraDbContext dbContext,
    IUploadProvider uploadProvider,
    IResumeService resumeService,
    IJobDescriptionService jobDescriptionService,
    IResumeAnalysisService resumeAnalysisService,
    IInterviewSessionService interviewSessionService,
    IInterviewFlowService interviewFlowService,
    IInterviewAnswerService interviewAnswerService,
    IInterviewReportService interviewReportService,
    IPracticeDashboardService practiceDashboardService,
    IPracticeJobProcessor jobProcessor,
    InterviewPersistence persistence,
    TimeProvider timeProvider) : IPracticeService
{
    private const string DevelopmentResumeAnalysisOperation = "development-resume-analysis.create";
    private const string ResumeExtractionFailureMessage = "Không thể đọc nội dung CV. Vui lòng thử lại với file PDF hoặc DOCX rõ hơn.";
}
