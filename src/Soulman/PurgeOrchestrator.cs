using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Soulman;

/// <summary>
/// Handles the full purge/blacklist flow:
///   - Resolves absolute local paths to sync-relative paths (Music/x, Movies/x, TV/x)
///   - Deletes files locally for purge operations
///   - Broadcasts PURGE messages to all discovered peers
///   - Receives and applies incoming peer PURGE messages
///   - Prevents message loops via seen-message-ID tracking
/// </summary>
public class PurgeOrchestrator
{
    private readonly ILogger<PurgeOrchestrator> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;
    private readonly InstanceDiscovery _discovery;
    private readonly BlacklistManager _blacklist;

    // Loop prevention: msgId → when we first saw it. Entries expire after 1 hour.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenMsgIds = new(StringComparer.OrdinalIgnoreCase);

    public PurgeOrchestrator(
        ILogger<PurgeOrchestrator> logger,
        IOptionsMonitor<SoulmanSettings> options,
        InstanceDiscovery discovery,
        BlacklistManager blacklist)
    {
        _logger = logger;
        _options = options;
        _discovery = discovery;
        _blacklist = blacklist;
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all configured sync root absolute paths.
    /// </summary>
    public string[] GetSyncRootPaths()
    {
        var s = _options.CurrentValue;
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.DestinationPath)) roots.Add(Path.GetFullPath(s.DestinationPath!));
        if (!string.IsNullOrWhiteSpace(s.MovieDestinationPath)) roots.Add(Path.GetFullPath(s.MovieDestinationPath!));
        if (!string.IsNullOrWhiteSpace(s.TvDestinationPath)) roots.Add(Path.GetFullPath(s.TvDestinationPath!));
        return roots.ToArray();
    }

    /// <summary>
    /// Adds a path to the blacklist (sync filter only — no deletion, no broadcast).
    /// Accepts a full absolute path; resolves to sync-relative automatically.
    /// </summary>
    public (bool Success, string Message) Blacklist(string absolutePath)
    {
        var syncPath = ResolveToSyncPath(absolutePath);
        if (syncPath == null)
            return (false, $"'{absolutePath}' is not inside any Soulman sync folder (Music, Movies, TV).");
        return _blacklist.Add(syncPath);
    }

    /// <summary>
    /// Adds to blacklist, deletes locally, and broadcasts PURGE to all peers.
    /// </summary>
    public async Task<(bool Success, string Message)> PurgeAsync(string absolutePath, CancellationToken token = default)
    {
        var syncPath = ResolveToSyncPath(absolutePath);
        if (syncPath == null)
            return (false, $"'{absolutePath}' is not inside any Soulman sync folder (Music, Movies, TV).");

        _blacklist.Add(syncPath);

        try
        {
            if (Directory.Exists(absolutePath))
            {
                Directory.Delete(absolutePath, recursive: true);
                _logger.LogInformation("Purged directory {Path}", absolutePath);
            }
            else if (File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
                _logger.LogInformation("Purged file {Path}", absolutePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete {Path} during purge", absolutePath);
            return (false, $"Failed to delete '{absolutePath}': {ex.Message}");
        }

        var msgId = Guid.NewGuid().ToString("N");
        _seenMsgIds[msgId] = DateTimeOffset.UtcNow;

        await BroadcastPurgeAsync(syncPath, msgId, token);

        return (true, $"Purged '{syncPath}' locally and notified peers.");
    }

    /// <summary>
    /// Called by SyncServer when an incoming PURGE command arrives from a peer.
    /// Deletes the local file if present. Returns false if this message was already processed (loop guard).
    /// Does NOT re-broadcast — the originator is responsible for reaching all peers.
    /// </summary>
    public bool HandleIncomingPurge(string syncPath, string msgId)
    {
        if (!_seenMsgIds.TryAdd(msgId, DateTimeOffset.UtcNow))
        {
            _logger.LogDebug("Ignoring duplicate PURGE message {MsgId}", msgId);
            return false;
        }

        CleanupExpiredMsgIds();

        var (isValid, reason) = PurgePathPolicy.Validate(syncPath);
        if (!isValid)
        {
            _logger.LogWarning("Incoming PURGE path '{Path}' rejected by policy: {Reason}", syncPath, reason);
            return false;
        }

        // Also add to local blacklist so it doesn't get re-synced
        _blacklist.Add(syncPath);

        var localPath = ResolveSyncPathToAbsolute(syncPath, _options.CurrentValue);
        if (localPath == null)
        {
            _logger.LogDebug("Incoming PURGE '{Path}' — no matching sync root configured, skipping deletion", syncPath);
            return true;
        }

        try
        {
            if (Directory.Exists(localPath))
            {
                Directory.Delete(localPath, recursive: true);
                _logger.LogInformation("Peer PURGE: deleted directory {Path}", localPath);
            }
            else if (File.Exists(localPath))
            {
                File.Delete(localPath);
                _logger.LogInformation("Peer PURGE: deleted file {Path}", localPath);
            }
            else
            {
                _logger.LogDebug("Peer PURGE: path not present locally — nothing to delete: {Path}", localPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peer PURGE: failed to delete {Path}", localPath);
        }

        return true;
    }

    // ── Path Resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Converts an absolute local path to a sync-relative path (e.g. "Music/Artist/Album").
    /// Returns null if the path is not under any configured sync root.
    /// </summary>
    public string? ResolveToSyncPath(string absolutePath)
    {
        var settings = _options.CurrentValue;
        var abs = Path.GetFullPath(absolutePath);

        if (!string.IsNullOrWhiteSpace(settings.DestinationPath))
        {
            var rel = RelativeIfUnder(abs, Path.GetFullPath(settings.DestinationPath!));
            if (rel != null) return $"Music/{rel}";
        }
        if (!string.IsNullOrWhiteSpace(settings.MovieDestinationPath))
        {
            var rel = RelativeIfUnder(abs, Path.GetFullPath(settings.MovieDestinationPath!));
            if (rel != null) return $"Movies/{rel}";
        }
        if (!string.IsNullOrWhiteSpace(settings.TvDestinationPath))
        {
            var rel = RelativeIfUnder(abs, Path.GetFullPath(settings.TvDestinationPath!));
            if (rel != null) return $"TV/{rel}";
        }
        return null;
    }

    // ── Private Helpers ────────────────────────────────────────────────────

    private async Task BroadcastPurgeAsync(string syncPath, string msgId, CancellationToken token)
    {
        IReadOnlyCollection<DiscoveredInstance> peers;
        try
        {
            peers = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(3), token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Peer discovery failed during purge broadcast");
            return;
        }

        _logger.LogInformation("Broadcasting PURGE '{Path}' to {Count} peer(s)", syncPath, peers.Count);

        var tasks = peers.Select(p => SendPurgeToPeerAsync(p, syncPath, msgId, token));
        await Task.WhenAll(tasks);
    }

    private async Task SendPurgeToPeerAsync(DiscoveredInstance peer, string syncPath, string msgId, CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            await client.ConnectAsync(peer.EndPoint.Address, peer.SyncPort, cts.Token);
            await using var stream = client.GetStream();
            await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            await writer.WriteLineAsync($"PURGE {syncPath} {msgId}");
            var response = await reader.ReadLineAsync(cts.Token);
            _logger.LogInformation("PURGE sent to {Machine}: {Response}", peer.MachineName, response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send PURGE to {Machine}", peer.MachineName);
        }
    }

    private void CleanupExpiredMsgIds()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var kv in _seenMsgIds.Where(x => x.Value < cutoff).ToList())
            _seenMsgIds.TryRemove(kv.Key, out _);
    }

    private static string? RelativeIfUnder(string path, string root)
    {
        // Ensure both end with separator for reliable prefix check
        var p = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return null;

        var rel = path[r.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rel.Replace('\\', '/');
    }

    private static string? ResolveSyncPathToAbsolute(string syncPath, SoulmanSettings settings)
    {
        var norm = syncPath.Replace('\\', '/').TrimStart('/');
        var slash = norm.IndexOf('/');
        if (slash <= 0) return null;

        var prefix = norm[..slash].ToUpperInvariant();
        var rest = norm[(slash + 1)..];

        return prefix switch
        {
            "MUSIC"  when !string.IsNullOrWhiteSpace(settings.DestinationPath)      => Path.Combine(settings.DestinationPath!, rest),
            "MOVIES" when !string.IsNullOrWhiteSpace(settings.MovieDestinationPath) => Path.Combine(settings.MovieDestinationPath!, rest),
            "TV"     when !string.IsNullOrWhiteSpace(settings.TvDestinationPath)    => Path.Combine(settings.TvDestinationPath!, rest),
            _ => null
        };
    }
}
