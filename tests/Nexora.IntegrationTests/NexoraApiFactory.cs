using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexora.Business.Ai;
using Nexora.Business.Email;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class NexoraApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source=nexora-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
    private readonly SqliteConnection _connection;
    private readonly object _databaseLock = new();
    private readonly IAiProvider _aiProvider;
    private readonly IReadOnlyDictionary<string, string?>? _configurationOverrides;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly string _environment = "Testing";

    public NexoraApiFactory() : this((IAiProvider?)null, null) { }

    internal NexoraApiFactory(IAiProvider aiProvider) : this(aiProvider, null) { }

    internal NexoraApiFactory(IReadOnlyDictionary<string, string?> configurationOverrides) : this((IAiProvider?)null, configurationOverrides) { }
    internal NexoraApiFactory(IReadOnlyDictionary<string, string?> configurationOverrides, Action<IServiceCollection> configureServices) : this((IAiProvider?)null, configurationOverrides, configureServices) { }

    internal NexoraApiFactory(string environment) : this(environment, new Dictionary<string, string?>())
    { }

    internal NexoraApiFactory(string environment, IReadOnlyDictionary<string, string?> configurationOverrides) :
        this((IAiProvider?)null, MergeEnvironmentOverrides(environment, configurationOverrides)) => _environment = environment;

    private static Dictionary<string, string?> MergeEnvironmentOverrides(string environment, IReadOnlyDictionary<string, string?> overrides)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Features:Ai"] = "false",
            ["Features:Payment"] = "false",
            ["Features:Upload"] = "false",
            ["Authentication:Jwt:Issuer"] = "Nexora.Tests",
            ["Authentication:Jwt:Audience"] = "Nexora.Tests.Client",
            ["Authentication:Jwt:SigningKey"] = "integration-test-signing-key-32-characters-minimum",
            ["Ai:Provider"] = "gemini",
            ["Ai:Gemini:ApiKey"] = "test-only-not-used",
            ["Ai:Gemini:Model"] = "test-gemini-model",
            ["Billing:Payment:Provider"] = "fake"
        };
        if (environment is "Staging" or "Production")
        {
            dict["Authentication:EmailVerification:PublicUrl"] = "https://staging.nexora.app";
            dict["Email:Provider"] = "resend";
            dict["Email:FromAddress"] = "support@nexora.app";
            dict["Email:FromName"] = "Nexora";
            dict["Email:Resend:ApiKey"] = "re_staging_test_api_key_12345";
            dict["Email:Resend:ApiBaseUrl"] = "https://api.resend.com";
        }
        foreach (var (key, value) in overrides)
        {
            dict[key] = value;
        }
        return dict;
    }

    private NexoraApiFactory(IAiProvider? aiProvider, IReadOnlyDictionary<string, string?>? configurationOverrides, Action<IServiceCollection>? configureServices = null)
    {
        _aiProvider = aiProvider ?? new TestAiProvider();
        _configurationOverrides = configurationOverrides;
        _configureServices = configureServices;
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
        });
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=nexora_tests",
                ["Authentication:Jwt:SigningKey"] = "integration-test-signing-key-32-characters-minimum",
                ["Authentication:Jwt:Issuer"] = "Nexora.Tests",
                ["Authentication:Jwt:Audience"] = "Nexora.Tests.Client",
                ["Authentication:EmailVerification:PublicUrl"] = "http://localhost:3000",
                ["Authentication:EmailVerification:TokenLifespanHours"] = "24",
                ["Billing:Payment:Provider"] = "fake",
                ["Billing:FakePayment:WebhookSecret"] = "phase2-test-webhook-key-material",
                ["Billing:FakePayment:TimestampToleranceMinutes"] = "5",
                ["Ai:Provider"] = "gemini",
                ["Realtime:Enabled"] = "false",
                ["Ai:Gemini:ApiKey"] = "test-only-not-used",
                ["Ai:Gemini:Model"] = "test-gemini-model",
                ["RateLimits:Authentication:PermitLimit"] = "1000",
                ["RateLimits:PasswordRecovery:PermitLimit"] = "1000",
                ["RateLimits:LoginEmail:PermitLimit"] = "1000",
                ["Storage:Local:RootPath"] = Path.Combine(Path.GetTempPath(), "nexora-api-tests")
            };
            if (_configurationOverrides is not null)
                foreach (var item in _configurationOverrides) values[item.Key] = item.Value;
            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<NexoraDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NexoraDbContext>>();
            services.AddDbContext<NexoraDbContext>(options => options.UseSqlite(_connectionString));
            services.RemoveAll<IAiProvider>();
            services.AddSingleton(_aiProvider);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender, RecordingEmailSender>();
            _configureServices?.Invoke(services);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (_environment != "Testing")
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(_configurationOverrides!)
                {
                    // Supply startup configuration before Program reads it; never use a live dev database/provider.
                    ["ConnectionStrings:Postgres"] = "Host=localhost;Database=nexora_tests"
                }));
        return base.CreateHost(builder);
    }

    public HttpClient CreateHttpsClient(bool handleCookies = true) => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = handleCookies,
        AllowAutoRedirect = false
    });

    public void InitializeDatabase()
    {
        lock (_databaseLock)
        {
            using var scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.EnsureCreated();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
