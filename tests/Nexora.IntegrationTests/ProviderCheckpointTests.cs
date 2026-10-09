using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Business.Ai;
using Nexora.Business.Common;
using Nexora.Data.Persistence;
using Npgsql;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class ProviderCheckpointTests
{
    [Fact]
    public async Task RestartReplaysRecordedFailureThenSuccessfulSecondAttemptWithoutAnyPaidCall()
    {
        var provider = new TestAiProvider();
        provider.EnqueueResponse(AiPurposes.InterviewFirstQuestion, new AiProviderException(AiProviderFailureKind.Unavailable, "Controlled first attempt failure"));
        using var factory = new NexoraApiFactory(provider);
        factory.InitializeDatabase();
        var executor = factory.Services.GetRequiredService<IStructuredAiExecutor>();
        var context = new AiOperationContext("two-attempts", Guid.NewGuid(), JobId: Guid.NewGuid());
        var original = await executor.ExecuteAsync(AiOperations.InterviewFirstQuestion, "question-topic: technical", context, CancellationToken.None);
        Assert.Equal(2, provider.TotalCalls);
        var restarted = new StructuredAiExecutor(provider, NullLogger<StructuredAiExecutor>.Instance,
            ActivatorUtilities.CreateInstance<Nexora.Data.Practice.ProviderAdmission>(factory.Services));
        var replay = await restarted.ExecuteAsync(AiOperations.InterviewFirstQuestion, "question-topic: technical", context, CancellationToken.None);
        Assert.Equal(original.Value, replay.Value);
        Assert.Equal(2, provider.TotalCalls);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().ProviderCallReservations.CountAsync());
    }

    [Fact]
    public Task SuccessfulCallSurvivesCompletionOutageAndExecutorRestart() => RunCompletionOutageAsync(false);

    [PostgresFact]
    public Task SuccessfulCallSurvivesRealPostgresCompletionOutageAndExecutorRestart() => RunCompletionOutageAsync(true);

    private static async Task RunCompletionOutageAsync(bool postgres)
    {
        var failure = new FailCheckpointOnce();
        var provider = new TestAiProvider();
        provider.EnqueueHandler(AiPurposes.InterviewFirstQuestion, request =>
        {
            failure.Armed = true; // Provider has returned a usable response/usage.
            request.UsageObserver?.Invoke(new AiTokenUsage(100, 50));
            return new GeneratedQuestion("Bạn sẽ thiết kế một REST API như thế nào?");
        });
        using var factory = postgres
            ? NexoraApiFactory.CreatePostgres(Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!, provider, failure)
            : new NexoraApiFactory(provider, null, dbInterceptors: [failure]);
        factory.InitializeDatabase();
        var gate = factory.Services.GetRequiredService<IProviderAdmission>();
        var executor = new StructuredAiExecutor(provider, NullLogger<StructuredAiExecutor>.Instance, gate);
        var context = new AiOperationContext("checkpoint", Guid.NewGuid(), JobId: Guid.NewGuid());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await executor.ExecuteAsync(AiOperations.InterviewFirstQuestion, "question-topic: technical", context, deadline.Token);
        Assert.Equal(1, failure.Failures);
        Assert.Equal(1, provider.TotalCalls);
        using (var scope = factory.Services.CreateScope())
        {
            var record = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().ProviderCallReservations.SingleAsync();
            Assert.NotNull(record.CompletedAt);
            Assert.NotNull(record.ResultJson);
            Assert.Equal(100, record.ActualPromptTokens);
            Assert.True(record.ReservedTokens > 0);
        }
        // New admission/executor instances: no reliance on an in-memory replay cache.
        var restartedGate = ActivatorUtilities.CreateInstance<Nexora.Data.Practice.ProviderAdmission>(factory.Services);
        var restarted = new StructuredAiExecutor(provider, NullLogger<StructuredAiExecutor>.Instance, restartedGate);
        var replay = await restarted.ExecuteAsync(AiOperations.InterviewFirstQuestion, "question-topic: technical",
            context with { CorrelationId = "worker-restarted" }, deadline.Token);
        Assert.Equal(result.Value, replay.Value);
        Assert.Equal(1, provider.TotalCalls);
        await Assert.ThrowsAsync<BusinessException>(() => restarted.ExecuteAsync(AiOperations.InterviewFirstQuestion,
            "changed input", context, deadline.Token));
        using var verify = factory.Services.CreateScope();
        Assert.Equal(1, await verify.ServiceProvider.GetRequiredService<NexoraDbContext>().ProviderCallReservations.CountAsync());
    }

    private sealed class FailCheckpointOnce : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int Failures { get; private set; }

        private void MaybeFail(DbCommand command)
        {
            if (!Armed || !command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                !command.CommandText.Contains("provider_call_reservations", StringComparison.Ordinal)) return;
            Armed = false;
            Failures++;
            throw new NpgsqlException("Controlled checkpoint transport failure", new IOException("Controlled reset"));
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            MaybeFail(command);
            return ValueTask.FromResult(result);
        }
    }
}
