using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Privacy;
using Nexora.Data;
using Nexora.Data.Persistence;
using Nexora.Data.Privacy;

namespace Nexora.UnitTests.Privacy;

public sealed class RetentionOptionsTests
{
    [Fact]
    public async Task DefaultsDisableCleanupWithoutOpeningADatabaseConnection()
    {
        var settings = new RetentionOptions();
        Assert.False(settings.Enabled);
        Assert.True(settings.DryRun);
        Assert.False(settings.PurgeEnabled);
        Assert.Equal(12, settings.AuditRetentionMonths);
        await using var db = new NexoraDbContext(new DbContextOptionsBuilder<NexoraDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused").Options);
        var processor = new RetentionProcessor(db, Options.Create(settings), TimeProvider.System, NullLogger<RetentionProcessor>.Instance);
        Assert.Equal("disabled", (await processor.RunDueAsync(CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("AuditRetentionMonths", "11")]
    [InlineData("BatchSize", "1001")]
    [InlineData("SweepIntervalHours", "0")]
    [InlineData("VerificationGraceHours", "25")]
    [InlineData("FailureBackoffMinutes", "0")]
    [InlineData("MaxConsecutiveFailures", "0")]
    public void UnsafeConfigurationFailsValidation(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused;Username=unused",
            [$"Privacy:Retention:{key}"] = value
        }).Build();
        using var services = new ServiceCollection().AddDataPersistence(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<RetentionOptions>>().Value);
    }
}
