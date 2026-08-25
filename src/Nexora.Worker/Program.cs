using Nexora.Business;
using Nexora.Data;
using Nexora.Integrations;
using Nexora.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddBusiness();
builder.Services.AddDataPersistence(builder.Configuration);
builder.Services.AddIntegrations(builder.Configuration);
builder.Services.AddHostedService<PracticeWorker>();

var host = builder.Build();
host.Run();
