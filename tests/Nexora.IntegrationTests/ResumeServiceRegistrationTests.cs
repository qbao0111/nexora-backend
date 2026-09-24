using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Practice;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class ResumeServiceRegistrationTests
{
    [Fact]
    public async Task PracticeFacadeResolvesAndDelegatesResumeJobDescriptionAndAnalysisReads()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var userId = Guid.NewGuid();

        var practice = Assert.IsType<PracticeService>(services.GetRequiredService<IPracticeService>());
        Assert.Same(practice, services.GetRequiredService<IPracticeJobProcessor>());
        Assert.IsType<ResumeService>(services.GetRequiredService<IResumeService>());
        Assert.IsType<JobDescriptionService>(services.GetRequiredService<IJobDescriptionService>());
        Assert.IsType<ResumeAnalysisService>(services.GetRequiredService<IResumeAnalysisService>());

        Assert.Empty(await practice.GetResumesAsync(userId, CancellationToken.None));
        Assert.Empty(await practice.GetJobDescriptionsAsync(userId, CancellationToken.None));
        Assert.Empty((await practice.GetResumeAnalysisHistoryAsync(userId, 1, 20, CancellationToken.None)).Items);
    }
}
