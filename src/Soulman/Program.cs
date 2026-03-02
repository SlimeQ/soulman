using System.Threading;
using Soulman;

// Handle CLI commands for blacklist management (Linux only, runs before app starts)
if (!OperatingSystem.IsWindows() && args.Length > 0)
{
    var exitCode = HandleBlacklistCli(args);
    Environment.Exit(exitCode);
}

const string mutexName = "Global\\Soulman.Instance";
using var singleInstance = new Mutex(initiallyOwned: true, name: mutexName, out var isNewInstance);

if (!isNewInstance)
{
    Console.WriteLine("Soulman is already running; exiting duplicate instance.");
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
builder.Services.AddSingleton<BlacklistManager>();
builder.Services.AddSingleton<DownloadScanner>();
builder.Services.AddSingleton<CloneFolderStore>();
builder.Services.AddSingleton<PathPreferenceStore>();
builder.Services.AddSingleton<MoveNotificationBroker>();
builder.Services.AddSingleton<MoveLogStore>();
builder.Services.AddSingleton<TransferProgressBroker>();
builder.Services.AddSingleton<SyncServer>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SyncServer>());
builder.Services.AddSingleton<SyncClient>();
builder.Services.AddHostedService<SyncWorker>();
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
        singleInstance.ReleaseMutex();
    }
}

// CLI handler for blacklist management (Linux only)
static int HandleBlacklistCli(string[] args)
{
    var configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "soulman");
    
    var configPath = Path.Combine(configDir, "appsettings.json");
    
    // Simple config read helper
    static SoulmanSettings? LoadSettings(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Soulman", out var soulman))
            {
                var settings = new SoulmanSettings();
                if (soulman.TryGetProperty("PurgedPaths", out var purged))
                {
                    settings.PurgedPaths = purged.EnumerateArray()
                        .Select(e => e.GetString() ?? "")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToArray();
                }
                return settings;
            }
        }
        catch { }
        return null;
    }
    
    // Simple config save helper
    static void SaveSettings(string path, string[] purgedPaths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        
        // Read existing config and update just the PurgedPaths
        var existingSettings = LoadSettings(path);
        var settings = new
        {
            Soulman = new
            {
                SourcePath = existingSettings?.SourcePath,
                DestinationPath = existingSettings?.DestinationPath,
                MovieDestinationPath = existingSettings?.MovieDestinationPath,
                TvDestinationPath = existingSettings?.TvDestinationPath,
                AdditionalSources = existingSettings?.AdditionalSources ?? new List<string>(),
                PollIntervalSeconds = existingSettings?.PollIntervalSeconds ?? 30,
                SettledSeconds = existingSettings?.SettledSeconds ?? 20,
                PurgedPaths = purgedPaths
            }
        };
        
        var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        
        File.WriteAllText(path, json);
    }
    
    // Normalize path helper
    static string NormalizePath(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
    
    string? command = null;
    string? pathArg = null;
    
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] is "--add-blacklist" or "-a" && i + 1 < args.Length)
        {
            command = "add";
            pathArg = args[++i];
        }
        else if (args[i] is "--remove-blacklist" or "-r" && i + 1 < args.Length)
        {
            command = "remove";
            pathArg = args[++i];
        }
        else if (args[i] is "--list-blacklist" or "-l")
        {
            command = "list";
        }
        else if (args[i] is "--clear-blacklist" or "-c")
        {
            command = "clear";
        }
        else if (args[i] is "--help" or "-h")
        {
            command = "help";
        }
    }
    
    if (command == null)
    {
        Console.WriteLine($"Unknown command: {string.Join(" ", args)}");
        Console.WriteLine("Use --help for usage information.");
        return 1;
    }
    
    switch (command)
    {
        case "help":
            Console.WriteLine("Soulman Blacklist CLI");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  --add-blacklist, -a <path>    Add a path to the blacklist");
            Console.WriteLine("  --remove-blacklist, -r <path> Remove a path from the blacklist");
            Console.WriteLine("  --list-blacklist, -l          List all blacklisted paths");
            Console.WriteLine("  --clear-blacklist, -c         Clear all blacklisted paths");
            Console.WriteLine("  --help, -h                    Show this help message");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  Soulman --add-blacklist Music/Movies");
            Console.WriteLine("  Soulman --add-blacklist \"Music/Music\"");
            Console.WriteLine("  Soulman --list-blacklist");
            Console.WriteLine("  Soulman --remove-blacklist Music/Movies");
            Console.WriteLine();
            Console.WriteLine("Valid paths are scoped like: Music/something, Movies/something, TV/something");
            return 0;
            
        case "list":
            var settings = LoadSettings(configPath);
            var purgedPaths = settings?.PurgedPaths ?? Array.Empty<string>();
            if (purgedPaths.Length == 0)
            {
                Console.WriteLine("Blacklist is empty.");
            }
            else
            {
                Console.WriteLine("Blacklisted paths:");
                foreach (var p in purgedPaths)
                {
                    Console.WriteLine($"  {p}");
                }
            }
            return 0;
            
        case "add":
            if (string.IsNullOrWhiteSpace(pathArg))
            {
                Console.WriteLine("Error: Path argument is required for --add-blacklist");
                return 1;
            }
            
            var normalizedAdd = NormalizePath(pathArg);
            var validation = PurgePathPolicy.Validate(normalizedAdd);
            if (!validation.IsValid)
            {
                Console.WriteLine($"Error: {validation.Reason}");
                return 1;
            }
            
            var addSettings = LoadSettings(configPath);
            var addList = (addSettings?.PurgedPaths ?? Array.Empty<string>()).ToList();
            
            if (addList.Contains(normalizedAdd, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"'{normalizedAdd}' is already blacklisted.");
                return 0;
            }
            
            addList.Add(normalizedAdd);
            SaveSettings(configPath, addList.ToArray());
            Console.WriteLine($"Added '{normalizedAdd}' to blacklist.");
            return 0;
            
        case "remove":
            if (string.IsNullOrWhiteSpace(pathArg))
            {
                Console.WriteLine("Error: Path argument is required for --remove-blacklist");
                return 1;
            }
            
            var normalizedRemove = NormalizePath(pathArg);
            var removeSettings = LoadSettings(configPath);
            var removeList = (removeSettings?.PurgedPaths ?? Array.Empty<string>()).ToList();
            
            var existing = removeList.FirstOrDefault(p => 
                string.Equals(p, normalizedRemove, StringComparison.OrdinalIgnoreCase));
            
            if (existing == null)
            {
                Console.WriteLine($"'{normalizedRemove}' is not in the blacklist.");
                return 1;
            }
            
            removeList.Remove(existing);
            SaveSettings(configPath, removeList.ToArray());
            Console.WriteLine($"Removed '{normalizedRemove}' from blacklist.");
            return 0;
            
        case "clear":
            var clearSettings = LoadSettings(configPath);
            var clearList = clearSettings?.PurgedPaths ?? Array.Empty<string>();
            
            if (clearList.Length == 0)
            {
                Console.WriteLine("Blacklist is already empty.");
                return 0;
            }
            
            var count = clearList.Length;
            SaveSettings(configPath, Array.Empty<string>());
            Console.WriteLine($"Cleared blacklist ({count} paths removed).");
            return 0;
            
        default:
            Console.WriteLine($"Unknown command: {command}");
            return 1;
    }
}
