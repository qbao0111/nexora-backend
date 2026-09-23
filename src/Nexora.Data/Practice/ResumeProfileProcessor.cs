using System.Text.Json;
using Nexora.Business.Ai;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed class ResumeProfileProcessor(
    NexoraDbContext dbContext,
    IResumeContextBuilder resumeContextBuilder,
    IAiProvider aiProvider,
    IStructuredAiExecutor structuredAiExecutor,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string ProfilePromptVersion => AiOperations.ResumeProfile.PromptVersion;
    private static string ProfileSchemaVersion => AiOperations.ResumeProfile.SchemaVersion;
    private string CurrentModelVersion => string.IsNullOrWhiteSpace(aiProvider.ModelVersion)
        ? throw new InvalidOperationException("The configured AI provider must expose a model version.")
        : aiProvider.ModelVersion.Trim();

    internal async Task<ResumeProfile> EnsureResumeProfileAsync(
        ResumeRecord resume, Guid correlationId, CancellationToken cancellationToken)
    {
        if (resume.ProfilePromptVersion == ProfilePromptVersion &&
            resume.ProfileSchemaVersion == ProfileSchemaVersion &&
            resume.ProfileModelVersion == CurrentModelVersion)
        {
            var existing = TryReadResumeProfile(resume.StructuredProfile);
            if (existing is not null) return existing;
        }

        if (string.IsNullOrWhiteSpace(resume.ExtractedText)) throw InvalidAiOutput();
        var context = resumeContextBuilder.BuildProfileExtractionContext(resume.ExtractedText);
        ResumeProfile profile;
        try
        {
            var execResult = await structuredAiExecutor.ExecuteAsync(
                AiOperations.ResumeProfile,
                context,
                new AiOperationContext(correlationId.ToString("N")),
                cancellationToken);
            profile = execResult.Value;
        }
        catch (AiProviderException exception)
        {
            throw AiUnavailable(exception);
        }

        resume.StructuredProfile = JsonSerializer.Serialize(profile, JsonOptions);
        resume.ProfileModelVersion = CurrentModelVersion;
        resume.ProfilePromptVersion = ProfilePromptVersion;
        resume.ProfileSchemaVersion = ProfileSchemaVersion;
        resume.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return profile;
    }

    internal static ResumeProfile? TryReadResumeProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var validation = ResumeProfileValidator.NormalizeAndValidate(JsonSerializer.Deserialize<ResumeProfile>(value, JsonOptions));
            return validation.IsValid ? validation.NormalizedValue : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static void ValidateResumeProfile(ResumeProfile profile)
    {
        if (!ResumeProfileValidator.NormalizeAndValidate(profile).IsValid) throw InvalidAiOutput();
    }

    private static BusinessException InvalidAiOutput() => new("AI_OUTPUT_INVALID", "AI trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);
    private static BusinessException AiUnavailable(AiProviderException exception) => new(
        exception.Kind == AiProviderFailureKind.RateLimited ? "AI_RATE_LIMITED" : "AI_PROVIDER_UNAVAILABLE",
        "Dịch vụ AI tạm thời chưa sẵn sàng. Vui lòng thử lại sau.",
        BusinessErrorKind.ExternalFailure);
}
