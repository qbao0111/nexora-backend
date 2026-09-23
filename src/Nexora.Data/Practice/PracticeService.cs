using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class PracticeService(
    NexoraDbContext dbContext,
    IUploadProvider uploadProvider,
    IStorageProvider storageProvider,
    IDetailedDocumentExtractor detailedDocumentExtractor,
    IDocumentOcrProvider documentOcrProvider,
    IResumeContextBuilder resumeContextBuilder,
    IAiProvider aiProvider,
    IStructuredAiExecutor structuredAiExecutor,
    IBillingService billingService,
    IFeatureEntitlementService featureEntitlementService,
    TimeProvider timeProvider,
    ILogger<PracticeService> logger) : IPracticeService, IPracticeJobProcessor
{
    private const int MaximumHistoryPageSize = 100;
    private const string DevelopmentResumeAnalysisOperation = "development-resume-analysis.create";
    private const string JobDescriptionCreateOperation = "job-description.create";
    private const string PromptVersion = "phase3-v1";
    private const string SchemaVersion = "phase3-star-v2";
    private static string ProfilePromptVersion => AiOperations.ResumeProfile.PromptVersion;
    private static string ProfileSchemaVersion => AiOperations.ResumeProfile.SchemaVersion;
    private const string RubricVersion = "interview-rubric-star-v2";
    private const string Disclaimer = "Điểm số chỉ là ước lượng phục vụ coaching, không phải đánh giá tuyển dụng.";
    private const int MinimumReportAnswers = 2;
    private const string ResumeExtractionFailureMessage = "Không thể đọc nội dung CV. Vui lòng thử lại với file PDF hoặc DOCX rõ hơn.";
    private static readonly JsonDocument EmptySchema = JsonDocument.Parse("{}");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonDocument QuestionSchema = JsonDocument.Parse("""{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""");
    private static readonly JsonDocument AnalysisSchema = JsonDocument.Parse("""{"type":"object","properties":{"strengths":{"type":"array","items":{"type":"string"}},"gaps":{"type":"array","items":{"type":"string"}},"recommendations":{"type":"array","items":{"type":"string"}}},"required":["strengths","gaps","recommendations"]}""");
    private static readonly JsonDocument ProfileSchema = JsonDocument.Parse("""{"type":"object","properties":{"summary":{"type":"string"},"skills":{"type":"array","items":{"type":"string"}},"experiences":{"type":"array","items":{"type":"object","properties":{"company":{"type":"string"},"role":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},"highlights":{"type":"array","items":{"type":"string"}}},"required":["company","role","start","end","highlights"]}},"education":{"type":"array","items":{"type":"object","properties":{"institution":{"type":"string"},"degree":{"type":"string"},"start":{"type":"string"},"end":{"type":"string"},"details":{"type":"array","items":{"type":"string"}}},"required":["institution","degree","start","end","details"]}},"projects":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"role":{"type":"string"},"technologies":{"type":"array","items":{"type":"string"}},"highlights":{"type":"array","items":{"type":"string"}}},"required":["name","role","technologies","highlights"]}},"certifications":{"type":"array","items":{"type":"string"}},"languages":{"type":"array","items":{"type":"string"}}},"required":["summary","skills","experiences","education","projects","certifications","languages"]}""");
    private static readonly JsonDocument EvaluationSchema = JsonDocument.Parse("""{"type":"object","properties":{"scores":{"type":"array","items":{"type":"object","properties":{"criterion":{"type":"string"},"score":{"type":"integer"},"evidence":{"type":"string"}},"required":["criterion","score","evidence"]}},"feedback":{"type":"string"},"star":{"type":"object","properties":{"applicable":{"type":"boolean"},"overallScore":{"type":"integer"},"situation":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"task":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"action":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"result":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"missingElements":{"type":"array","items":{"type":"string"}},"strengths":{"type":"array","items":{"type":"string"}},"coachingTips":{"type":"array","items":{"type":"string"}}},"required":["applicable"]}},"required":["scores","feedback","star"]}""");
    private static readonly JsonDocument ReportSchema = JsonDocument.Parse("""{"type":"object","properties":{"scores":{"type":"array","items":{"type":"object","properties":{"criterion":{"type":"string"},"score":{"type":"integer"},"evidence":{"type":"string"}},"required":["criterion","score","evidence"]}},"strengths":{"type":"array","items":{"type":"string"}},"gaps":{"type":"array","items":{"type":"string"}},"actionPlan":{"type":"array","items":{"type":"string"}}},"required":["scores","strengths","gaps","actionPlan"]}""");
    private string CurrentModelVersion => string.IsNullOrWhiteSpace(aiProvider.ModelVersion)
        ? throw new InvalidOperationException("The configured AI provider must expose a model version.")
        : aiProvider.ModelVersion.Trim();

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string Bound(string? value) => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, 20_000)];
    private static BusinessException Validation(string message, string code = "VALIDATION_ERROR") => new(code, message, BusinessErrorKind.Validation);
    private static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    private static BusinessException CareerGoalNotFound() => new("CAREER_GOAL_NOT_FOUND", "Không tìm thấy career goal.", BusinessErrorKind.NotFound);
    private static BusinessException Conflict(string code, string message) => new(code, message, BusinessErrorKind.Conflict);
    private static BusinessException InvalidState() => Conflict("INVALID_INTERVIEW_STATE", "Trạng thái interview không hợp lệ cho thao tác này.");
    private static BusinessException InterviewUpgradeRequired() =>
        new("INTERVIEW_UPGRADE_REQUIRED", "Hãy nâng cấp gói để tiếp tục phiên phỏng vấn này.", BusinessErrorKind.Forbidden);
    private static BusinessException InterviewLimitReached() =>
        Conflict("INTERVIEW_MAX_QUESTIONS_REACHED", "Phiên phỏng vấn đã đạt giới hạn câu hỏi.");
    private static BusinessException InvalidAiOutput() => new("AI_OUTPUT_INVALID", "AI trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);
    private static BusinessException AiUnavailable(AiProviderException exception) => new(
        exception.Kind == AiProviderFailureKind.RateLimited ? "AI_RATE_LIMITED" : "AI_PROVIDER_UNAVAILABLE",
        "Dịch vụ AI tạm thời chưa sẵn sàng. Vui lòng thử lại sau.",
        BusinessErrorKind.ExternalFailure);
}
