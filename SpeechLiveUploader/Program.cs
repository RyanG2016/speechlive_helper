using SpeechLiveUploader;

// Initialize shutdown diagnostics early to capture any startup crashes
ShutdownDiagnostics.Initialize();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Speech Live Uploader Service";
});

var host = builder.Build();

// Register host lifetime events for shutdown tracking
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
ShutdownDiagnostics.RegisterHostLifetimeEvents(lifetime);

host.Run();
