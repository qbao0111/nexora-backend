namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class InterviewRecoveryPostgresTests
{
    [PostgresFact]
    public Task FiveAnswersRecoverWithoutUserRetryAndCompleteZeroStrengthReportOnPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        return PracticeApiTests.RunFiveAnswerRecoveryScenarioAsync(aiProvider =>
            NexoraApiFactory.CreatePostgres(connectionString, aiProvider));
    }
}
