using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Soulman;

// ── CLI subcommand check ────────────────────────────────────────────────
// `soulman blacklist <full_path>`  — add to blacklist (sync filter, no deletion)
// `soulman purge    <full_path>`   — add to blacklist + delete locally + broadcast to peers
// `soulman install-overlay`         — show instructions to register shell icon overlay
// `soulman uninstall-overlay`       — show instructions to unregister shell icon overlay
// `soulman help`                   — usage
// Works on both Windows and Linux.
var subCommand = args.Length > 0 ? args[0].ToLowerInvariant() : null;
if (subCommand is "blacklist" or "purge" or "install-overlay" or "uninstall-overlay" or "help")
{
    var exitCode = await HandleCliAsync(args);
    Environment.Exit(exitCode);
}

// ── Single-instance guard ───────────────────────────────────────────────
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

// ── Host / service setup ────────────────────────────────────────────────
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
builder.Services.AddSingleton<DownloadFilterManager>();
builder.Services.AddSingleton<PurgeOrchestrator>();
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

// Wire PurgeOrchestrator into SyncServer (avoids circular DI:
//   SyncServer ← PurgeOrchestrator ← InstanceDiscovery ← SyncServer)
host.Services.GetRequiredService<SyncServer>()
    .SetPurgeOrchestrator(host.Services.GetRequiredService<PurgeOrchestrator>());

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

// ══════════════════════════════════════════════════════════════════════════
// CLI implementation — runs in a lightweight mode without the full host
// ══════════════════════════════════════════════════════════════════════════

static async Task<int> HandleCliAsync(string[] args)
{
    var command   = args[0].ToLowerInvariant();
    var inputPath = args.Length > 1 ? string.Join(" ", args.Skip(1)) : null;

    // Overlay commands don't require settings
    if (command == "install-overlay")
    {
        var (success, message) = ShellOverlayHelper.InstallOverlay(inputPath);
        CliOutput(message, isError: !success);
        return success ? 0 : 1;
    }
    
    if (command == "uninstall-overlay")
    {
        var (success, message) = ShellOverlayHelper.UninstallOverlay();
        CliOutput(message, isError: !success);
        return success ? 0 : 1;
    }

    if (command == "help" || (command is "blacklist" or "purge" && string.IsNullOrWhiteSpace(inputPath)))
    {
        CliOutput("Soulman — Blacklist & Purge CLI", isError: false);
        CliOutput("", isError: false);
        CliOutput("Usage:", isError: false);
        CliOutput("  soulman blacklist <full_path>      Add path to sync blacklist (no deletion)", isError: false);
        CliOutput("  soulman purge    <full_path>      Blacklist + delete locally + broadcast to peers", isError: false);
        CliOutput("", isError: false);
        CliOutput("  soulman install-overlay [dll]    Show instructions for shell icon overlay", isError: false);
        CliOutput("  soulman uninstall-overlay        Show instructions to remove icon overlay", isError: false);
        CliOutput("", isError: false);
        CliOutput("Examples:", isError: false);
        CliOutput("  soulman blacklist \"/srv/media-library/Music/Movies\"", isError: false);
        CliOutput("  soulman purge \"/srv/media-library/Music/Movies\"", isError: false);
        CliOutput("", isError: false);
        CliOutput("Paths must be inside a configured sync root (Music, Movies, or TV).", isError: false);
        return command == "help" ? 0 : 1;
    }

    // Load settings from platform config path
    var configPath = GetConfigFilePath();
    var settings   = LoadCliSettings(configPath);

    if (settings == null)
    {
        CliOutput($"Could not load Soulman config from '{configPath}'.", isError: true);
        return 1;
    }

    // Resolve the absolute path to a sync-relative path
    var absolutePath = Path.GetFullPath(inputPath!.Trim('"', '\''));
    var syncPath     = ResolveToSyncPath(absolutePath, settings);

    if (syncPath == null)
    {
        CliOutput($"'{absolutePath}' is not inside any configured sync root.", isError: true);
        CliOutput("Sync roots:", isError: true);
        if (!string.IsNullOrWhiteSpace(settings.DestinationPath))
            CliOutput($"  Music   → {settings.DestinationPath}", isError: true);
        if (!string.IsNullOrWhiteSpace(settings.MovieDestinationPath))
            CliOutput($"  Movies  → {settings.MovieDestinationPath}", isError: true);
        if (!string.IsNullOrWhiteSpace(settings.TvDestinationPath))
            CliOutput($"  TV      → {settings.TvDestinationPath}", isError: true);
        return 1;
    }

    // Validate the sync-relative path
    var (isValid, reason) = PurgePathPolicy.Validate(syncPath);
    if (!isValid)
    {
        CliOutput($"Path rejected by safety policy: {reason}", isError: true);
        return 1;
    }

    // ── blacklist ──
    if (command == "blacklist")
    {
        var msg = CliAddToBlacklist(configPath, syncPath);
        CliOutput(msg, isError: false);
        
        // Notify Windows Explorer to refresh overlays
        if (OperatingSystem.IsWindows())
            ShellOverlayHelper.NotifyRefreshAll();
        
        return 0;
    }

    // ── purge ──
    // 1. Add to blacklist
    CliAddToBlacklist(configPath, syncPath);

    // 2. Delete locally
    try
    {
        if (Directory.Exists(absolutePath))
        {
            Directory.Delete(absolutePath, recursive: true);
            CliOutput($"Deleted directory: {absolutePath}", isError: false);
        }
        else if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
            CliOutput($"Deleted file: {absolutePath}", isError: false);
        }
        else
        {
            CliOutput($"Note: path not found locally (already gone?): {absolutePath}", isError: false);
        }
    }
    catch (Exception ex)
    {
        CliOutput($"Failed to delete '{absolutePath}': {ex.Message}", isError: true);
        return 1;
    }

    // 3. Discover peers and broadcast PURGE
    CliOutput("Discovering peers...", isError: false);
    var msgId = Guid.NewGuid().ToString("N");
    var peers = await CliDiscoverPeersAsync(TimeSpan.FromSeconds(4));

    if (peers.Count == 0)
    {
        CliOutput("No peers found on the network.", isError: false);
    }
    else
    {
        CliOutput($"Broadcasting PURGE to {peers.Count} peer(s)...", isError: false);
        foreach (var (address, syncPort, machine) in peers)
        {
            var ok = await CliSendPurgeAsync(address, syncPort, syncPath, msgId);
            CliOutput(ok ? $"  [OK] {machine}" : $"  [FAIL] {machine}", isError: !ok);
        }
    }

    CliOutput($"Purge complete: '{syncPath}'", isError: false);
    
    // Notify Windows Explorer to refresh overlays
    if (OperatingSystem.IsWindows())
        ShellOverlayHelper.NotifyRefreshAll();
    
    return 0;
}

// ── CLI helpers ────────────────────────────────────────────────────────────

/// Outputs a message to console and, on Windows when no console is attached, queues for MessageBox.
static void CliOutput(string message, bool isError)
{
    if (isError) Console.Error.WriteLine(message);
    else         Console.WriteLine(message);
    // Windows MessageBox is shown at the end by the caller if needed
}

/// Returns the platform-appropriate config file path.
static string GetConfigFilePath()
{
    var dir = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Soulman")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "soulman");
    return Path.Combine(dir, "appsettings.json");
}

/// Loads SoulmanSettings from the config JSON (minimal — only what CLI needs).
static SoulmanSettings? LoadCliSettings(string configPath)
{
    if (!File.Exists(configPath)) return null;
    try
    {
        using var stream = File.OpenRead(configPath);
        using var doc    = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("Soulman", out var sec)) return null;

        var s = new SoulmanSettings();
        if (sec.TryGetProperty("DestinationPath",      out var v)) s.DestinationPath      = v.GetString();
        if (sec.TryGetProperty("MovieDestinationPath", out     v)) s.MovieDestinationPath = v.GetString();
        if (sec.TryGetProperty("TvDestinationPath",    out     v)) s.TvDestinationPath    = v.GetString();
        if (sec.TryGetProperty("SourcePath",           out     v)) s.SourcePath           = v.GetString();
        if (sec.TryGetProperty("PollIntervalSeconds",  out     v)) s.PollIntervalSeconds  = v.GetInt32();
        if (sec.TryGetProperty("SettledSeconds",       out     v)) s.SettledSeconds       = v.GetInt32();
        if (sec.TryGetProperty("AdditionalSources", out v))
        {
            s.AdditionalSources = v.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToList();
        }
        if (sec.TryGetProperty("PurgedPaths", out v))
        {
            s.PurgedPaths = v.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToArray();
        }
        if (sec.TryGetProperty("DownloadFilters", out v))
        {
            var filters = new DownloadFilterSettings();

            if (v.TryGetProperty("AllowMusic", out var filterProp))
            {
                filters.AllowMusic = filterProp.GetBoolean();
            }

            if (v.TryGetProperty("AllowMovies", out filterProp))
            {
                filters.AllowMovies = filterProp.GetBoolean();
            }

            if (v.TryGetProperty("AllowTv", out filterProp))
            {
                filters.AllowTv = filterProp.GetBoolean();
            }

            if (v.TryGetProperty("BlockedPeers", out filterProp))
            {
                filters.BlockedPeers = filterProp.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(x => x.Length > 0)
                    .ToArray();
            }

            if (v.TryGetProperty("BlockedFolders", out filterProp))
            {
                filters.BlockedFolders = filterProp.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(x => x.Length > 0)
                    .ToArray();
            }

            s.DownloadFilters = DownloadFilterPolicy.Clone(filters);
        }
        return s;
    }
    catch { return null; }
}

/// Resolves an absolute local path to a sync-relative path (e.g. "Music/Artist/Album").
static string? ResolveToSyncPath(string absPath, SoulmanSettings s)
{
    static string? Rel(string path, string? root, string prefix)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = path[r.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
        return $"{prefix}/{rel}";
    }
    return Rel(absPath, s.DestinationPath, "Music")
        ?? Rel(absPath, s.MovieDestinationPath, "Movies")
        ?? Rel(absPath, s.TvDestinationPath, "TV");
}

/// Adds a sync-relative path to PurgedPaths in the config file. Returns a status message.
static string CliAddToBlacklist(string configPath, string syncPath)
{
    var s       = LoadCliSettings(configPath) ?? new SoulmanSettings();
    var current = (s.PurgedPaths ?? Array.Empty<string>()).ToList();
    if (current.Contains(syncPath, StringComparer.OrdinalIgnoreCase))
        return $"'{syncPath}' is already blacklisted.";

    current.Add(syncPath);
    CliSaveBlacklist(configPath, s, current.ToArray());
    return $"Added '{syncPath}' to blacklist.";
}

/// Saves updated PurgedPaths back to the config file preserving all other fields.
static void CliSaveBlacklist(string configPath, SoulmanSettings s, string[] purgedPaths)
{
    Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
    var payload = JsonSerializer.Serialize(new
    {
        Soulman = new
        {
            s.SourcePath,
            s.DestinationPath,
            s.MovieDestinationPath,
            s.TvDestinationPath,
            AdditionalSources = s.AdditionalSources ?? new List<string>(),
            s.PollIntervalSeconds,
            s.SettledSeconds,
            PurgedPaths = purgedPaths,
            DownloadFilters = DownloadFilterPolicy.Clone(s.DownloadFilters)
        }
    }, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });
    File.WriteAllText(configPath, payload);
}

/// Simple standalone UDP peer discovery (no running service required).
/// Returns list of (IPAddress, SyncPort, MachineName).
static async Task<List<(IPAddress Address, int SyncPort, string Machine)>> CliDiscoverPeersAsync(TimeSpan timeout)
{
    const string MagicHeader = "SOULMAN_DISCOVERY_V1";
    var requestId    = Guid.NewGuid().ToString("N");
    var requestMsg   = $"{MagicHeader}:REQUEST:{requestId}:PORT:0";
    var requestBytes = Encoding.UTF8.GetBytes(requestMsg);

    var results = new List<(IPAddress, int, string)>();
    using var udp = new UdpClient(AddressFamily.InterNetwork);
    udp.EnableBroadcast = true;
    udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

    // Broadcast probes
    var broadcastTargets = new IPEndPoint[]
    {
        new(IPAddress.Broadcast, 45832),
        new(IPAddress.Parse("239.255.64.64"), 45832)
    };

    foreach (var ep in broadcastTargets)
    {
        try { await udp.SendAsync(requestBytes, ep); } catch { /* best effort */ }
    }

    // Collect responses
    using var cts = new CancellationTokenSource(timeout);
    var responsePrefix = $"{MagicHeader}:RESPONSE:{requestId}:";
    try
    {
        while (!cts.IsCancellationRequested)
        {
            var recv = await udp.ReceiveAsync(cts.Token);
            var msg  = Encoding.UTF8.GetString(recv.Buffer);
            if (!msg.StartsWith(responsePrefix, StringComparison.Ordinal)) continue;

            var payload = msg[responsePrefix.Length..];
            var parts   = payload.Split('|');
            var machine = parts.ElementAtOrDefault(0) ?? "";
            if (int.TryParse(parts.ElementAtOrDefault(2) ?? "0", out var port) && port > 0)
                results.Add((recv.RemoteEndPoint.Address, port, machine));
        }
    }
    catch (OperationCanceledException) { /* timeout — done */ }

    return results;
}

/// Sends a PURGE command over TCP to a single peer. Returns true on success.
static async Task<bool> CliSendPurgeAsync(IPAddress address, int port, string syncPath, string msgId)
{
    try
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync(address, port, cts.Token);
        await using var stream = tcp.GetStream();
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        using var reader       = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync($"PURGE {syncPath} {msgId}");
        var response = await reader.ReadLineAsync(cts.Token);
        return response?.StartsWith("OK") == true;
    }
    catch { return false; }
}
