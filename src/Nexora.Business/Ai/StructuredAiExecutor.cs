using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nexora.Business.Common;

namespace Nexora.Business.Ai;

public sealed partial class StructuredAiExecutor(IAiProvider aiProvider, ILogger<StructuredAiExecutor> logger) : IStructuredAiExecutor
{
    private const int GlobalMaxAttemptsPerPurpose = 2;

    public async Task<AiExecutionResult<T>> ExecuteAsync<T>(
        AiOperationDefinition<T> operation,
        string untrustedInput,
        AiOperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();
        var modelVersion = aiProvider.ModelVersion;
        var correlationId = context.CorrelationId;
        var instructions = operation.Instructions;
        var maxAttempts = Math.Clamp(operation.MaxAttempts, 1, GlobalMaxAttemptsPerPurpose);

        T? currentSemanticRaw = default;
        AiValidationResult<T>? currentValidation = null;
        T? previousSemanticRaw = default;
        AiValidationResult<T>? previousValidation = null;
        AiProviderException? lastProviderException = null;
        AiReasoningEffortOverride? reasoningOverride = null;
        var outputTruncationRetry = false;
        var malformedStructuredOutputRetry = false;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var isSemanticRepairAttempt = attempt > 1 && currentValidation is not null && !currentValidation.IsValid;
            var isMalformedOutputRepairAttempt = attempt > 1 && malformedStructuredOutputRetry;
            var isRepairAttempt = isSemanticRepairAttempt || isMalformedOutputRepairAttempt;
            var currentInstructions = isSemanticRepairAttempt
                ? operation.BuildRepairInstructions(currentValidation!, instructions)
                : isMalformedOutputRepairAttempt
                    ? operation.BuildMalformedStructuredOutputRepairInstructions(instructions)
                    : instructions;
            var currentReasoningOverride = isSemanticRepairAttempt
                ? operation.GetSemanticRepairReasoningOverride(modelVersion)
                : reasoningOverride;
            reasoningOverride = null;
            malformedStructuredOutputRetry = false;

            var request = new AiRequest(
                operation.Purpose,
                operation.PromptVersion,
                modelVersion,
                operation.RubricVersion,
                operation.SchemaVersion,
                untrustedInput,
                operation.OutputSchema,
                operation.GetEffectiveMaxOutputTokens(attempt, outputTruncationRetry),
                correlationId,
                currentInstructions,
                currentReasoningOverride);
            outputTruncationRetry = false;

            if (currentReasoningOverride is not null)
            {
                if (isSemanticRepairAttempt)
                    LogSemanticRepairReasoningOverride(logger, operation.Purpose,
                        currentReasoningOverride.Value.ToString().ToLowerInvariant(), attempt, correlationId);
                else
                    LogReasoningFallbackRetry(logger, operation.Purpose,
                        currentReasoningOverride.Value.ToString().ToLowerInvariant(), attempt, correlationId);
            }

            try
            {
                var raw = await aiProvider.GenerateStructuredAsync<T>(request, cancellationToken);
                var validation = operation.NormalizeAndValidate(raw, context);
                previousSemanticRaw = currentSemanticRaw;
                previousValidation = currentValidation;
                currentSemanticRaw = raw;
                currentValidation = validation;

                if (validation.IsValid)
                {
                    LogExecutionSucceeded(
                        logger,
                        operation.Purpose,
                        modelVersion,
                        request.MaxOutputTokens,
                        ReasoningMode(currentReasoningOverride),
                        attempt,
                        isRepairAttempt,
                        stopwatch.ElapsedMilliseconds,
                        correlationId);
                    if (isRepairAttempt)
                    {
                        LogOutputRepaired(logger, operation.Purpose, attempt, correlationId);
                    }

                    if (validation.NormalizedValue is AnswerEvaluation eval && eval.Star?.Applicable == true)
                    {
                        if (eval.Scores.All(s => s.Score >= 75) && eval.Star.Action?.Detected == false && eval.Star.Result?.Detected == false)
                        {
                            LogCrossFieldSuspicious(logger, operation.Purpose, correlationId);
                        }
                    }

                    return new AiExecutionResult<T>(
                        validation.NormalizedValue!,
                        modelVersion,
                        operation.PromptVersion,
                        operation.SchemaVersion,
                        operation.RubricVersion,
                        isRepairAttempt,
                        attempt,
                        stopwatch.ElapsedMilliseconds);
                }

                LogValidationFailed(
                    logger,
                    operation.Purpose,
                    modelVersion,
                    request.MaxOutputTokens,
                    ReasoningMode(currentReasoningOverride),
                    validation.FailureReason ?? "unknown",
                    validation.ValidationStage ?? "validation",
                    attempt,
                    validation.Repairable,
                    correlationId);

                if (!validation.Repairable || attempt >= maxAttempts)
                {
                    var recovery = operation.TryRecoverTerminalValidation(raw, context, validation);
                    if (recovery is { IsValid: true })
                    {
                        LogTerminalValidationRecovered(
                            logger,
                            operation.Purpose,
                            validation.FailureReason ?? "unknown",
                            attempt,
                            correlationId);

                        return new AiExecutionResult<T>(
                            recovery.NormalizedValue!,
                            modelVersion,
                            operation.PromptVersion,
                            operation.SchemaVersion,
                            operation.RubricVersion,
                            isRepairAttempt,
                            attempt,
                            stopwatch.ElapsedMilliseconds);
                    }

                    if (isRepairAttempt &&
                        attempt >= maxAttempts &&
                        previousSemanticRaw is not null &&
                        previousValidation is { IsValid: false } priorValidation)
                    {
                        var priorRecovery = operation.TryRecoverTerminalValidation(
                            previousSemanticRaw,
                            context,
                            priorValidation);
                        if (priorRecovery is { IsValid: true })
                        {
                            LogTerminalSemanticRepairRecovered(
                                logger,
                                operation.Purpose,
                                priorValidation.FailureReason ?? "unknown",
                                validation.FailureReason ?? "unknown",
                                attempt,
                                correlationId);

                            return new AiExecutionResult<T>(
                                priorRecovery.NormalizedValue!,
                                modelVersion,
                                operation.PromptVersion,
                                operation.SchemaVersion,
                                operation.RubricVersion,
                                true,
                                attempt,
                                stopwatch.ElapsedMilliseconds);
                        }
                    }

                    LogExecutionTerminalFailure(
                        logger,
                        operation.Purpose,
                        modelVersion,
                        request.MaxOutputTokens,
                        ReasoningMode(currentReasoningOverride),
                        validation.FailureReason ?? "unknown",
                        validation.ValidationStage ?? "validation",
                        attempt,
                        correlationId);

                    throw new BusinessException("AI_OUTPUT_INVALID", "Dữ liệu phản hồi từ AI không hợp lệ.", BusinessErrorKind.ExternalFailure);
                }
            }
            catch (AiProviderException ex)
            {
                lastProviderException = ex;
                var retryReason = ex.RetryHint switch
                {
                    AiProviderRetryHint.LowerReasoningEffort => "reasoning_budget_exhausted",
                    AiProviderRetryHint.OutputTruncated => "output_truncated",
                    AiProviderRetryHint.MalformedStructuredOutput => "malformed_structured_output",
                    _ => "provider_failure"
                };
                LogProviderRequestFailed(
                    logger,
                    operation.Purpose,
                    modelVersion,
                    ex.Kind.ToString(),
                    request.MaxOutputTokens,
                    ReasoningMode(currentReasoningOverride),
                    ex.RetryHint.ToString(),
                    retryReason,
                    attempt,
                    stopwatch.ElapsedMilliseconds,
                    correlationId);

                if (ex.Kind == AiProviderFailureKind.InvalidResponse &&
                    isRepairAttempt &&
                    attempt >= maxAttempts &&
                    !cancellationToken.IsCancellationRequested &&
                    currentSemanticRaw is not null &&
                    currentValidation is { IsValid: false } priorValidation)
                {
                    var recovery = operation.TryRecoverTerminalValidation(
                        currentSemanticRaw,
                        context,
                        priorValidation);
                    if (recovery is { IsValid: true })
                    {
                        LogSemanticRepairProviderFailureRecovered(
                            logger,
                            operation.Purpose,
                            priorValidation.FailureReason ?? "unknown",
                            ex.Kind.ToString(),
                            attempt,
                            correlationId);

                        return new AiExecutionResult<T>(
                            recovery.NormalizedValue!,
                            modelVersion,
                            operation.PromptVersion,
                            operation.SchemaVersion,
                            operation.RubricVersion,
                            true,
                            attempt,
                            stopwatch.ElapsedMilliseconds);
                    }
                }

                if (ex.Kind == AiProviderFailureKind.InvalidResponse &&
                    ex.RetryHint == AiProviderRetryHint.LowerReasoningEffort &&
                    currentValidation is null &&
                    attempt < maxAttempts)
                {
                    reasoningOverride = AiReasoningEffortOverride.Low;
                }
                else if (ex.Kind == AiProviderFailureKind.InvalidResponse &&
                    ex.RetryHint == AiProviderRetryHint.OutputTruncated &&
                    operation.SupportsOutputTruncationRetry &&
                    currentValidation is null &&
                    attempt < maxAttempts)
                {
                    LogOutputTruncationRetry(
                        logger,
                        operation.Purpose,
                        operation.GetEffectiveMaxOutputTokens(attempt + 1, outputTruncationRetry: true),
                        attempt + 1,
                        correlationId);
                    outputTruncationRetry = true;
                }
                else if (ex.Kind == AiProviderFailureKind.InvalidResponse &&
                    ex.RetryHint == AiProviderRetryHint.MalformedStructuredOutput &&
                    currentValidation is null &&
                    attempt < maxAttempts)
                {
                    malformedStructuredOutputRetry = true;
                }

                if (ex.Kind is AiProviderFailureKind.Configuration or AiProviderFailureKind.Authentication ||
                    cancellationToken.IsCancellationRequested ||
                    attempt >= maxAttempts ||
                    !IsRetryable(ex.Kind))
                {
                    LogProviderTerminalFailure(
                        logger,
                        operation.Purpose,
                        modelVersion,
                        request.MaxOutputTokens,
                        ReasoningMode(currentReasoningOverride),
                        ex.Kind.ToString(),
                        ex.RetryHint.ToString(),
                        attempt,
                        correlationId);

                    throw MapProviderException(ex);
                }
            }
        }

        if (lastProviderException is not null)
            throw MapProviderException(lastProviderException);

        throw new BusinessException("AI_OUTPUT_INVALID", "Dữ liệu phản hồi từ AI không hợp lệ.", BusinessErrorKind.ExternalFailure);
    }

    private static bool IsRetryable(AiProviderFailureKind kind) =>
        kind is AiProviderFailureKind.RateLimited or AiProviderFailureKind.Timeout or AiProviderFailureKind.Unavailable or AiProviderFailureKind.InvalidResponse;

    private static string ReasoningMode(AiReasoningEffortOverride? reasoningOverride) =>
        reasoningOverride?.ToString().ToLowerInvariant() ?? "configured";

    private static BusinessException MapProviderException(AiProviderException exception) => exception.Kind switch
    {
        AiProviderFailureKind.Authentication => new BusinessException("AI_PROVIDER_AUTH_FAILED", "Không thể xác thực với AI provider.", BusinessErrorKind.ExternalFailure),
        AiProviderFailureKind.Configuration => new BusinessException("AI_PROVIDER_CONFIGURATION_INVALID", "Cấu hình AI provider không hợp lệ.", BusinessErrorKind.ExternalFailure),
        AiProviderFailureKind.RateLimited => new BusinessException("AI_RATE_LIMITED", "AI provider đang bị giới hạn tốc độ.", BusinessErrorKind.ExternalFailure),
        AiProviderFailureKind.Timeout or AiProviderFailureKind.Unavailable => new BusinessException("AI_PROVIDER_UNAVAILABLE", "Dịch vụ AI tạm thời không khả dụng.", BusinessErrorKind.ExternalFailure),
        _ => new BusinessException("AI_OUTPUT_INVALID", "Dữ liệu phản hồi từ AI không hợp lệ.", BusinessErrorKind.ExternalFailure)
    };

    [LoggerMessage(LogLevel.Information, "AI structured execution succeeded: purpose={Purpose}, model={Model}, effectiveBudget={EffectiveBudget}, reasoningMode={ReasoningMode}, attempt={Attempt}, repairUsed={RepairUsed}, latencyMs={LatencyMs}, correlationId={CorrelationId}")]
    private static partial void LogExecutionSucceeded(ILogger logger, string purpose, string model, int effectiveBudget, string reasoningMode, int attempt, bool repairUsed, long latencyMs, string correlationId);

    [LoggerMessage(LogLevel.Warning, "AI structured validation failed: purpose={Purpose}, model={Model}, effectiveBudget={EffectiveBudget}, reasoningMode={ReasoningMode}, failureReason={FailureReason}, stage={Stage}, attempt={Attempt}, repairable={Repairable}, correlationId={CorrelationId}")]
    private static partial void LogValidationFailed(ILogger logger, string purpose, string model, int effectiveBudget, string reasoningMode, string failureReason, string stage, int attempt, bool repairable, string correlationId);

    [LoggerMessage(LogLevel.Error, "AI structured execution failed terminal: purpose={Purpose}, model={Model}, effectiveBudget={EffectiveBudget}, reasoningMode={ReasoningMode}, failureReason={FailureReason}, stage={Stage}, attempt={Attempt}, outcome=failed, correlationId={CorrelationId}")]
    private static partial void LogExecutionTerminalFailure(ILogger logger, string purpose, string model, int effectiveBudget, string reasoningMode, string failureReason, string stage, int attempt, string correlationId);

    [LoggerMessage(LogLevel.Warning, "AI provider request failed: purpose={Purpose}, model={Model}, failureKind={FailureKind}, effectiveBudget={EffectiveBudget}, reasoningMode={ReasoningMode}, retryHint={RetryHint}, retryReason={RetryReason}, attempt={Attempt}, latencyMs={LatencyMs}, correlationId={CorrelationId}")]
    private static partial void LogProviderRequestFailed(
        ILogger logger,
        string purpose,
        string model,
        string failureKind,
        int effectiveBudget,
        string reasoningMode,
        string retryHint,
        string retryReason,
        int attempt,
        long latencyMs,
        string correlationId);

    [LoggerMessage(LogLevel.Information, "AI structured retry after provider output truncation: purpose={Purpose}, effectiveBudget={EffectiveBudget}, attempt={Attempt}, retryReason=output_truncated, correlationId={CorrelationId}")]
    private static partial void LogOutputTruncationRetry(ILogger logger, string purpose, int effectiveBudget, int attempt, string correlationId);

    [LoggerMessage(LogLevel.Information, "AI structured retry using reasoning override: purpose={Purpose}, effectiveEffort={EffectiveEffort}, attempt={Attempt}, retryReason=reasoning_budget_exhausted, correlationId={CorrelationId}")]
    private static partial void LogReasoningFallbackRetry(ILogger logger, string purpose, string effectiveEffort, int attempt, string correlationId);

    [LoggerMessage(LogLevel.Information, "AI semantic repair using reasoning override: purpose={Purpose}, effectiveEffort={EffectiveEffort}, attempt={Attempt}, retryReason=semantic_repair, correlationId={CorrelationId}")]
    private static partial void LogSemanticRepairReasoningOverride(ILogger logger, string purpose, string effectiveEffort, int attempt, string correlationId);

    [LoggerMessage(LogLevel.Error, "AI provider request failed terminal: purpose={Purpose}, model={Model}, failureKind={FailureKind}, effectiveBudget={EffectiveBudget}, reasoningMode={ReasoningMode}, retryHint={RetryHint}, attempt={Attempt}, outcome=failed, correlationId={CorrelationId}")]
    private static partial void LogProviderTerminalFailure(ILogger logger, string purpose, string model, int effectiveBudget, string reasoningMode, string failureKind, string retryHint, int attempt, string correlationId);

    [LoggerMessage(LogLevel.Information, "AI output repaired successfully: purpose={Purpose}, attempt={Attempt}, correlationId={CorrelationId}")]
    private static partial void LogOutputRepaired(ILogger logger, string purpose, int attempt, string correlationId);

    [LoggerMessage(LogLevel.Warning, "AI terminal semantic validation recovered with a contract-safe fallback: purpose={Purpose}, failureReason={FailureReason}, attempt={Attempt}, correlationId={CorrelationId}")]
    private static partial void LogTerminalValidationRecovered(ILogger logger, string purpose, string failureReason, int attempt, string correlationId);

    [LoggerMessage(LogLevel.Warning, "AI semantic repair provider failure recovered from prior validated raw: purpose={Purpose}, priorFailureReason={PriorFailureReason}, providerFailureKind={ProviderFailureKind}, attempt={Attempt}, correlationId={CorrelationId}")]
    private static partial void LogSemanticRepairProviderFailureRecovered(
        ILogger logger,
        string purpose,
        string priorFailureReason,
        string providerFailureKind,
        int attempt,
        string correlationId);

    [LoggerMessage(LogLevel.Warning, "AI terminal semantic repair recovered from prior validated raw: purpose={Purpose}, previousFailureReason={PreviousFailureReason}, currentFailureReason={CurrentFailureReason}, attempt={Attempt}, correlationId={CorrelationId}")]
    private static partial void LogTerminalSemanticRepairRecovered(
        ILogger logger,
        string purpose,
        string previousFailureReason,
        string currentFailureReason,
        int attempt,
        string correlationId);

    [LoggerMessage(LogLevel.Warning, "AI cross-field evaluation suspicious: general rubric strong but multiple STAR components absent: purpose={Purpose}, correlationId={CorrelationId}")]
    private static partial void LogCrossFieldSuspicious(ILogger logger, string purpose, string correlationId);
}
