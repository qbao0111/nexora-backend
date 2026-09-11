using Nexora.Business;
using Nexora.Data;
using Nexora.Integrations;
using Nexora.Worker;
using Nexora.Worker.Observability;
using Sentry.Extensions.Logging;
using Sentry.Extensions.Logging.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOptions<SentryLoggingOptions>()
    .Configure<IConfiguration, IHostEnvironment>((options, configuration, environment) =>
        SentryObservability.Configure(options, environment, configuration));
builder.Services.AddSentry<SentryLoggingOptions>();
builder.Services.AddSingleton<IWorkerSentryReporter, WorkerSentryReporter>();
ProductionSafety.ValidateDevelopmentAdapters(
    builder.Environment.IsProduction(),
    builder.Configuration.GetValue("Features:Ai", true),
    builder.Configuration.GetValue("Features:Payment", true),
    builder.Configuration.GetValue("Features:Upload", true),
    builder.Configuration.GetValue<string?>("Storage:Provider"));
ProductionSafety.ValidateDeploymentConfiguration(builder.Environment.EnvironmentName, builder.Configuration);
builder.Services.AddOptions<WorkerPollingOptions>()
    .Bind(builder.Configuration.GetSection(WorkerPollingOptions.SectionName))
    .Validate(options => options.BusyDelayMilliseconds >= 0, "Busy delay must not be negative.")
    .Validate(options => options.IdleInitialDelayMilliseconds > 0, "Initial idle delay must be positive.")
    .Validate(options => options.IdleMaximumDelayMilliseconds >= options.IdleInitialDelayMilliseconds,
        "Maximum idle delay must be at least the initial idle delay.")
    .Validate(options => options.IdleBackoffMultiplier >= 1, "Idle backoff multiplier must be at least one.")
    .Validate(options => options.FailureDelayMilliseconds > 0, "Failure delay must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
    new AdaptivePollingBackoff(serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WorkerPollingOptions>>().Value));
builder.Services.AddBusiness();
builder.Services.AddDataPersistence(builder.Configuration);
builder.Services.AddIntegrations(builder.Configuration);
builder.Services.AddHostedService<PracticeWorker>();

var host = builder.Build();
host.Run();
