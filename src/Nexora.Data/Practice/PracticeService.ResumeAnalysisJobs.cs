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
    private async Task AnalyzeResumeAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var analysis = await dbContext.ResumeAnalyses.Include(item => item.Resume).Include(item => item.JobDescription)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);

        await using (var startTransaction = await BeginTransactionAsync(cancellationToken))
        {
            await LockUserAsync(analysis.UserId, cancellationToken);
            await dbContext.Entry(analysis.Resume).ReloadAsync(cancellationToken);
            if (analysis.Resume.DeletedAt is not null &&
                analysis.Status is PracticeValues.Queued or PracticeValues.Processing)
            {
                var now = timeProvider.GetUtcNow();
                analysis.Status = PracticeValues.Failed;
                analysis.ErrorCode = "RESUME_DELETED";
                analysis.UpdatedAt = now;
                EnqueueResourceChanged(analysis.UserId, "resumeAnalysis", analysis.Id, analysis.Status, now);
                MarkProcessed(job);
                if (analysis.UsageReservationId.HasValue)
                    await featureEntitlementService.VoidAsync(analysis.UserId, analysis.UsageReservationId.Value, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(startTransaction, cancellationToken);
                return;
            }

            if (analysis.Status == PracticeValues.Queued)
            {
                analysis.Status = PracticeValues.Processing;
                analysis.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            else if (analysis.Status != PracticeValues.Processing)
            {
                MarkProcessed(job);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(startTransaction, cancellationToken);
                return;
            }
            await CommitAsync(startTransaction, cancellationToken);
        }
        var profile = await EnsureResumeProfileAsync(analysis.Resume, analysis.Id, cancellationToken);
        analysis.ProfileSnapshot = JsonSerializer.Serialize(profile, JsonOptions);
        analysis.ProfileModelVersion = analysis.Resume.ProfileModelVersion;
        analysis.ProfilePromptVersion = analysis.Resume.ProfilePromptVersion;
        analysis.ProfileSchemaVersion = analysis.Resume.ProfileSchemaVersion;
        await dbContext.SaveChangesAsync(cancellationToken);

        var contextSnapshot = ReadAnalysisContext(analysis);
        if (!ResumeAnalysisModes.TryParse(contextSnapshot.Mode, out var analysisMode))
            throw Validation("Mode phân tích CV không hợp lệ.", "RESUME_ANALYSIS_MODE_INVALID");
        var input = resumeContextBuilder.BuildResumeAnalysisContext(profile, new ResumeAnalysisContext(
            analysisMode,
            analysis.JobDescription?.Content,
            contextSnapshot.Industry,
            contextSnapshot.TargetRole,
            contextSnapshot.Seniority));
        var operation = GetResumeAnalysisOperation(analysisMode);
        var execResult = await structuredAiExecutor.ExecuteAsync(
            operation,
            input,
            new AiOperationContext(
                analysis.Id.ToString("N"),
                analysis.UserId,
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ResumeAnalysisMetadata.Mode] = analysisMode.ToWireValue()
                }),
            cancellationToken);
        var result = execResult.Value;

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        analysis.Result = JsonSerializer.Serialize(result, JsonOptions);
        analysis.ModelVersion = execResult.ModelVersion;
        analysis.PromptVersion = execResult.PromptVersion;
        analysis.RubricVersion = execResult.RubricVersion;
        analysis.SchemaVersion = execResult.SchemaVersion;
        analysis.Status = PracticeValues.Completed;
        analysis.CompletedAt = analysis.UpdatedAt = timeProvider.GetUtcNow();
        EnqueueResourceChanged(analysis.UserId, "resumeAnalysis", analysis.Id, analysis.Status, analysis.UpdatedAt);
        MarkProcessed(job);
        if (analysis.UsageReservationId.HasValue)
            await featureEntitlementService.ConsumeAsync(analysis.UserId, analysis.UsageReservationId.Value, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

}
