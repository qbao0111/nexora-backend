using System.Text.Json;
using Nexora.Business.Ai;
using Nexora.Business.Common;

namespace Nexora.Data.Practice;

internal static class InterviewErrors
{
    internal const int MinimumReportAnswers = 2;
    internal const string Disclaimer = "Điểm số chỉ là ước lượng phục vụ coaching, không phải đánh giá tuyển dụng.";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);
    internal static BusinessException Validation(string message, string code = "VALIDATION_ERROR") => new(code, message, BusinessErrorKind.Validation);
    internal static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    internal static BusinessException Conflict(string code, string message) => new(code, message, BusinessErrorKind.Conflict);
    internal static BusinessException InvalidState() => Conflict("INVALID_INTERVIEW_STATE", "Trạng thái interview không hợp lệ cho thao tác này.");
    internal static BusinessException InterviewUpgradeRequired() =>
        new("INTERVIEW_UPGRADE_REQUIRED", "Hãy nâng cấp gói để tiếp tục phiên phỏng vấn này.", BusinessErrorKind.Forbidden);
    internal static BusinessException InterviewLimitReached() =>
        Conflict("INTERVIEW_MAX_QUESTIONS_REACHED", "Phiên phỏng vấn đã đạt giới hạn câu hỏi.");
    internal static BusinessException InvalidAiOutput() => new("AI_OUTPUT_INVALID", "AI trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);

    internal static AnswerEvaluation? TryDeserializeAnswerEvaluation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<AnswerEvaluation>(value, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
