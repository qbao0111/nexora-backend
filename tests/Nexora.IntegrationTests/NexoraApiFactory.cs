using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class NexoraApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public NexoraApiFactory() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=nexora_tests",
                ["Authentication:Jwt:SigningKey"] = "integration-test-signing-key-32-characters-minimum",
                ["Authentication:Jwt:Issuer"] = "Nexora.Tests",
                ["Authentication:Jwt:Audience"] = "Nexora.Tests.Client",
                ["Storage:Local:RootPath"] = Path.Combine(Path.GetTempPath(), "nexora-api-tests")
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<NexoraDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NexoraDbContext>>();
            services.AddSingleton(_connection);
            services.AddDbContext<NexoraDbContext>(options => options.UseSqlite(_connection));
        });
    }

    public HttpClient CreateHttpsClient(bool handleCookies = true) => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = handleCookies,
        AllowAutoRedirect = false
    });

    public void InitializeDatabase()
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.EnsureCreated();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
