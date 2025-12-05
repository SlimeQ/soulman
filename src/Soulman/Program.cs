using System.Threading;
using Soulman;

const string mutexName = "Global\\Soulman.Instance";
using var singleInstance = new Mutex(initiallyOwned: true, name: mutexName, out var isNewInstance);

if (!isNewInstance)
{
    Console.WriteLine("Soulman is already running; exiting duplicate instance.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "SOULMAN_");

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "Soulman");
}

builder.Services.Configure<SoulmanSettings>(builder.Configuration.GetSection("Soulman"));
builder.Services.AddSingleton<DownloadScanner>();
builder.Services.AddSingleton<CloneFolderStore>();
builder.Services.AddSingleton<PathPreferenceStore>();
builder.Services.AddSingleton<MoveNotificationBroker>();
builder.Services.AddSingleton<MoveLogStore>();
builder.Services.AddSingleton<InstanceDiscovery>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<InstanceDiscovery>());
builder.Services.AddHostedService<Worker>();
if (OperatingSystem.IsWindows() && Environment.UserInteractive)
{
    builder.Services.AddHostedService<TrayHostedService>();
}

var host = builder.Build();
try
{
    host.Run();
}
finally
{
    if (isNewInstance)
    {
        singleInstance.ReleaseMutex();
    }
}
