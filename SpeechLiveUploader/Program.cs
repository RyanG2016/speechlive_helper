using SpeechLiveUploader;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Speech Live Uploader Service";
});
var host = builder.Build();
host.Run();
