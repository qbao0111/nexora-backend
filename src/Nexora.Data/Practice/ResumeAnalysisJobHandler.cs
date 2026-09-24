using System.Data;
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

public sealed class ResumeAnalysisJobHandler(
    NexoraDbContext dbContext,
    IFeatureEntitlementService featureEntitlementService,
    IResumeContextBuilder resumeContextBuilder,
    IStructuredAiExecutor structuredAiExecutor,
    ResumeProfileProcessor resumeProfileProcessor,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    private static BusinessException Validation(string message, string code = "VALIDATION_ERROR") => new(code, message, BusinessErrorKind.Validation);
    private void MarkProcessed(OutboxEvent job) { job.Status = BillingValues.Processed; job.ProcessedAt = timeProvider.GetUtcNow(); }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null) throw NotFound();
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null) return null;
        return await dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
    }

    private static async Task CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is null) return;
        await transaction.CommitAsync(cancellationToken);
    }

    private void EnqueueResourceChanged(Guid userId, string resourceType, Guid resourceId, string status, DateTimeOffset occurredAt) =>
        dbContext.RealtimeNotifications.Add(new Nexora.Data.Realtime.RealtimeNotification
        {
            UserId = userId, ResourceType = resourceType, ResourceId = resourceId, Status = status, CreatedAt = occurredAt
        });

    internal Task ProcessAsync(OutboxEvent job, CancellationToken cancellationToken) => AnalyzeResumeAsync(job, cancellationToken);

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
        var profile = await resumeProfileProcessor.EnsureResumeProfileAsync(analysis.Resume, analysis.Id, cancellationToken);
        analysis.ProfileSnapshot = JsonSerializer.Serialize(profile, JsonOptions);
        analysis.ProfileModelVersion = analysis.Resume.ProfileModelVersion;
        analysis.ProfilePromptVersion = analysis.Resume.ProfilePromptVersion;
        analysis.ProfileSchemaVersion = analysis.Resume.ProfileSchemaVersion;
        await dbContext.SaveChangesAsync(cancellationToken);

        var contextSnapshot = ResumeAnalysisService.ReadAnalysisContext(analysis);
        if (!ResumeAnalysisModes.TryParse(contextSnapshot.Mode, out var analysisMode))
            throw Validation("Mode phân tích CV không hợp lệ.", "RESUME_ANALYSIS_MODE_INVALID");
        var input = resumeContextBuilder.BuildResumeAnalysisContext(profile, new ResumeAnalysisContext(
            analysisMode,
            analysis.JobDescription?.Content,
            contextSnapshot.Industry,
            contextSnapshot.TargetRole,
            contextSnapshot.Seniority));
        var operation = ResumeAnalysisService.GetResumeAnalysisOperation(analysisMode);
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

    internal async Task FailAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var current = await dbContext.OutboxEvents.SingleAsync(item => item.Id == job.Id, cancellationToken);
        current.Status = PracticeValues.Failed;
        current.ProcessedAt = timeProvider.GetUtcNow();
        var analysis = await dbContext.ResumeAnalyses.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
        analysis.Status = PracticeValues.Failed;
        analysis.ErrorCode = "AI_PROCESSING_FAILED";
        analysis.UpdatedAt = current.ProcessedAt.Value;
        EnqueueResourceChanged(analysis.UserId, "resumeAnalysis", analysis.Id, analysis.Status, analysis.UpdatedAt);
        if (analysis.UsageReservationId.HasValue)
            await featureEntitlementService.VoidAsync(analysis.UserId, analysis.UsageReservationId.Value, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

}
