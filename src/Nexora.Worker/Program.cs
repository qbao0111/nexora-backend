using Nexora.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<FoundationWorker>();

var host = builder.Build();
host.Run();
