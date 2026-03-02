using System.Threading;
using Soulman;

const string mutexName = "Global\\Soulman.Instance";
Mutex? singleInstance = null;
var isNewInstance = false;

try
{
    singleInstance = new Mutex(initiallyOwned: true, name: mutexName, out isNewInstance);
}
catch (UnauthorizedAccessException)
{
    isNewInstance = false;
}

if (!isNewInstance)
{
#if WINDOWS
    if (OperatingSystem.IsWindows() && Environment.UserInteractive)
    {
        System.Windows.Forms.MessageBox.Show(
            "Soulman is already running.",
            "Soulman",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Information);
    }
#endif

    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Load platform-appropriate config locations
if (!OperatingSystem.IsWindows())
{
    var configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "soulman");
    Directory.CreateDirectory(configDir);
    builder.Configuration.AddJsonFile(
        Path.Combine(configDir, "appsettings.json"), optional: true, reloadOnChange: true);
}

builder.Configuration
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "SOULMAN_");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddProvider(new Soulman.Logging.FileLoggerProvider());

#if WINDOWS
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "Soulman");
}
#else
if (OperatingSystem.IsLinux())
{
    builder.Services.AddSystemd();
}
#endif

builder.Services.Configure<SoulmanSettings>(builder.Configuration.GetSection("Soulman"));
builder.Services.AddSingleton<DownloadScanner>();
builder.Services.AddSingleton<CloneFolderStore>();
builder.Services.AddSingleton<PathPreferenceStore>();
builder.Services.AddSingleton<MoveNotificationBroker>();
builder.Services.AddSingleton<MoveLogStore>();
builder.Services.AddSingleton<TransferProgressBroker>();
builder.Services.AddSingleton<PurgeService>();
builder.Services.AddSingleton<SyncServer>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SyncServer>());
builder.Services.AddSingleton<SyncClient>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddHostedService<PurgeWorker>();
builder.Services.AddSingleton<InstanceDiscovery>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<InstanceDiscovery>());
builder.Services.AddHostedService<Worker>();

#if WINDOWS
if (OperatingSystem.IsWindows() && Environment.UserInteractive)
{
    builder.Services.AddHostedService<TrayHostedService>();
}
#endif

var host = builder.Build();
try
{
    host.Run();
}
finally
{
    if (isNewInstance)
    {
        singleInstance?.ReleaseMutex();
    }

    singleInstance?.Dispose();
}
