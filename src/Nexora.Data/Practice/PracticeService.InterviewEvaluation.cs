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
    private async Task EvaluateInterviewAnswerAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        await using (var transaction = await BeginTransactionAsync(cancellationToken))
        {
            var answer = await dbContext.InterviewAnswers
                .SingleOrDefaultAsync(item => item.Id == job.AggregateId, cancellationToken);
            if (answer is null || answer.EvaluationStatus is InterviewAnswerEvaluationStates.Ready or InterviewAnswerEvaluationStates.Failed)
            {
                MarkProcessed(job);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(transaction, cancellationToken);
                return;
            }

            answer.EvaluationStatus = InterviewAnswerEvaluationStates.Processing;
            answer.EvaluationErrorCode = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }

        var snapshot = await dbContext.InterviewAnswers
            .Include(item => item.Question)
            .Include(item => item.InterviewSession).ThenInclude(item => item.Resume)
            .Include(item => item.InterviewSession).ThenInclude(item => item.JobDescription)
            .Include(item => item.InterviewSession).ThenInclude(item => item.Answers)
            .SingleOrDefaultAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (snapshot is null) return;

        try
        {
            var question = snapshot.Question;
            var session = snapshot.InterviewSession;
            var isFollowUp = string.Equals(question.Kind, InterviewQuestionValues.Followup, StringComparison.Ordinal);
            var previousMissingElements = isFollowUp
                ? ReadMissingStarElements(session.Answers.FirstOrDefault(answer => answer.QuestionId == question.ParentQuestionId)?.Evaluation)
                : null;
            var hasUsableResumeContext = HasUsableResumeContext(session.Resume);
            var profile = hasUsableResumeContext
                ? TryReadResumeProfile(session.Resume?.StructuredProfile)
                : null;
            var answerContext = resumeContextBuilder.BuildAnswerEvaluationContext(
                session.Role,
                session.Seniority,
                session.InterviewType,
                session.JobDescription?.Content,
                question.Content,
                snapshot.Content,
                profile,
                question.Sequence,
                isFollowUp,
                previousMissingElements,
                question.Topic);
            var metadata = new Dictionary<string, string>
            {
                ["questionSequence"] = question.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isFollowup"] = isFollowUp ? "true" : "false",
                ["questionTopic"] = question.Topic
            };
            if (previousMissingElements is { Length: > 0 })
                metadata["followupTargetElements"] = string.Join(",", previousMissingElements);

            var result = await structuredAiExecutor.ExecuteAsync(
                AiOperations.InterviewEvaluate,
                answerContext,
                new AiOperationContext(
                    session.Id.ToString("N"),
                    snapshot.UserId,
                    ExpectedStar: question.Topic is InterviewQuestionValues.Behavioral or InterviewQuestionValues.BehavioralStar,
                    Metadata: metadata,
                    CandidateAnswer: snapshot.Content),
                cancellationToken);

            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var answer = await dbContext.InterviewAnswers.SingleAsync(item => item.Id == snapshot.Id, cancellationToken);
            if (answer.EvaluationStatus != InterviewAnswerEvaluationStates.Processing)
            {
                MarkProcessed(job);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(transaction, cancellationToken);
                return;
            }
            answer.Evaluation = JsonSerializer.Serialize(result.Value, JsonOptions);
            answer.EvaluationStatus = InterviewAnswerEvaluationStates.Ready;
            answer.EvaluationCompletedAt = timeProvider.GetUtcNow();
            answer.EvaluationErrorCode = null;
            EnqueueResourceChanged(snapshot.UserId, "interview", snapshot.InterviewSessionId, answer.EvaluationStatus, answer.EvaluationCompletedAt.Value);
            await dbContext.SaveChangesAsync(cancellationToken);
            await TryQueueReportIfReadyAsync(snapshot.InterviewSessionId, answer.EvaluationCompletedAt.Value, cancellationToken);
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await FailAnswerEvaluationAsync(job, snapshot, EvaluationErrorCode(exception), cancellationToken);
        }
    }

    private async Task FailAnswerEvaluationAsync(
        OutboxEvent job,
        InterviewAnswer snapshot,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var answer = await dbContext.InterviewAnswers.SingleOrDefaultAsync(item => item.Id == snapshot.Id, cancellationToken);
        if (answer is not null && answer.EvaluationStatus != InterviewAnswerEvaluationStates.Ready)
        {
            answer.EvaluationStatus = InterviewAnswerEvaluationStates.Failed;
            answer.Evaluation = null;
            answer.EvaluationErrorCode = errorCode;
            answer.EvaluationCompletedAt = timeProvider.GetUtcNow();
            EnqueueResourceChanged(answer.UserId, "interview", answer.InterviewSessionId, answer.EvaluationStatus, answer.EvaluationCompletedAt.Value);
        }
        job.Status = BillingValues.Failed;
        job.ProcessedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        EvaluationFailed(logger, AiOperations.InterviewEvaluate.Purpose, aiProvider.ModelVersion,
            snapshot.Id, snapshot.InterviewSessionId, errorCode, snapshot.Id.ToString("N"));
    }

    private static string EvaluationErrorCode(Exception exception) => exception switch
    {
        BusinessException { Code: "AI_PROVIDER_AUTH_FAILED" } => "AI_PROVIDER_AUTH_FAILED",
        BusinessException { Code: "AI_PROVIDER_CONFIGURATION_INVALID" } => "AI_PROVIDER_CONFIGURATION_INVALID",
        BusinessException { Code: "AI_RATE_LIMITED" } => "AI_RATE_LIMITED",
        BusinessException { Code: "AI_PROVIDER_UNAVAILABLE" } => "AI_PROVIDER_UNAVAILABLE",
        BusinessException { Code: "AI_OUTPUT_INVALID" } => "AI_OUTPUT_INVALID",
        AiProviderException { Kind: AiProviderFailureKind.Authentication } => "AI_PROVIDER_AUTH_FAILED",
        AiProviderException { Kind: AiProviderFailureKind.Configuration } => "AI_PROVIDER_CONFIGURATION_INVALID",
        AiProviderException { Kind: AiProviderFailureKind.RateLimited } => "AI_RATE_LIMITED",
        AiProviderException { Kind: AiProviderFailureKind.Timeout or AiProviderFailureKind.Unavailable } => "AI_PROVIDER_UNAVAILABLE",
        _ => "INTERNAL_PROCESSING_FAILED"
    };

}
