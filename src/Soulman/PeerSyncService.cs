using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Soulman;

/// <summary>
/// Handles peer-to-peer file synchronization between Soulman instances.
/// Each instance runs a TCP server to serve its library and periodically
/// pulls new files from discovered peers.
/// 
/// Protocol (all messages are newline-delimited JSON over TCP):
///   Client sends: {"type":"list"}                    → Server replies with file manifest
///   Client sends: {"type":"get","path":"rel/path"}   → Server streams the file bytes
///   Server push after move: UDP announce to peers
/// </summary>
public class PeerSyncService : IHostedService, IDisposable
{
    private const int SyncPort = 45833;
    private const string ManifestFileName = ".soulman-manifest.json";

    private readonly ILogger<PeerSyncService> _logger;
    private readonly InstanceDiscovery _discovery;
    private readonly MoveNotificationBroker _moveBroker;
    private readonly SoulmanSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _serverTask;
    private Task? _syncTask;

    // Relative path → SHA256 hash of known files in our library
    private readonly ConcurrentDictionary<string, FileManifestEntry> _manifest = new(StringComparer.OrdinalIgnoreCase);

    // Track what we've already synced from peers (relative paths)
    private readonly ConcurrentDictionary<string, bool> _synced = new(StringComparer.OrdinalIgnoreCase);

    private string? _libraryRoot;

    public PeerSyncService(
        ILogger<PeerSyncService> logger,
        InstanceDiscovery discovery,
        MoveNotificationBroker moveBroker,
        Microsoft.Extensions.Options.IOptionsMonitor<SoulmanSettings> options)
    {
        _logger = logger;
        _discovery = discovery;
        _moveBroker = moveBroker;
        _settings = options.CurrentValue;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryRoot = _settings.DestinationPath;
        if (string.IsNullOrWhiteSpace(_libraryRoot))
        {
            _logger.LogWarning("PeerSync: No destination path configured, sync disabled");
            return Task.CompletedTask;
        }

        // Build initial manifest
        RebuildManifest();

        // Subscribe to move notifications to update manifest
        _moveBroker.Subscribe(OnFileMoved);

        // Start TCP server
        _serverTask = Task.Run(() => RunServerAsync(_cts.Token), cancellationToken);

        // Start periodic sync loop
        _syncTask = Task.Run(() => SyncLoopAsync(_cts.Token), cancellationToken);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener?.Stop();
        _moveBroker.Unsubscribe(OnFileMoved);

        if (_serverTask != null)
        {
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
            catch { }
        }

        if (_syncTask != null)
        {
            try { await _syncTask.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
            catch { }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _listener?.Stop();
        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
    }

    #region Manifest

    private void RebuildManifest()
    {
        if (_libraryRoot == null || !Directory.Exists(_libraryRoot)) return;

        _manifest.Clear();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_libraryRoot, "*.*", SearchOption.AllDirectories))
            {
                if (_settings.IsSupportedFile(file))
                {
                    var relative = Path.GetRelativePath(_libraryRoot, file);
                    var info = new FileInfo(file);
                    _manifest[relative] = new FileManifestEntry(relative, info.Length);
                }
            }

            _logger.LogInformation("PeerSync: Manifest built with {Count} files", _manifest.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PeerSync: Error building manifest");
        }
    }

    private void OnFileMoved(int count, string destination)
    {
        // Rebuild manifest when files are moved (simple approach)
        RebuildManifest();
    }

    #endregion

    #region TCP Server

    private async Task RunServerAsync(CancellationToken token)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, SyncPort);
            _listener.Start();
            _logger.LogInformation("PeerSync: TCP server listening on port {Port}", SyncPort);

            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PeerSync: Accept failed");
                    continue;
                }

                // Handle each client on its own task
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                _logger.LogWarning(ex, "PeerSync: Server failed on port {Port}", SyncPort);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync();
                _logger.LogInformation("PeerSync: Received from {Remote}: '{Line}'", remote, line?.Substring(0, Math.Min(line?.Length ?? 0, 100)));
                if (string.IsNullOrWhiteSpace(line)) return;

                var msg = JsonSerializer.Deserialize<SyncMessage>(line);
                if (msg == null) return;
                _logger.LogInformation("PeerSync: Parsed message type='{Type}' from {Remote}", msg.Type, remote);

                switch (msg.Type)
                {
                    case "list":
                        await HandleListAsync(writer, token);
                        break;
                    case "get":
                        if (!string.IsNullOrWhiteSpace(msg.Path))
                            await HandleGetAsync(msg.Path, stream, writer, token);
                        break;
                    default:
                        _logger.LogDebug("PeerSync: Unknown message type '{Type}' from {Remote}", msg.Type, remote);
                        break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PeerSync: Error handling client {Remote}", remote);
            }
        }
    }

    private async Task HandleListAsync(StreamWriter writer, CancellationToken token)
    {
        var entries = _manifest.Values.ToArray();
        var json = JsonSerializer.Serialize(entries);
        await writer.WriteLineAsync(json);
    }

    private async Task HandleGetAsync(string relativePath, NetworkStream stream, StreamWriter writer,
        CancellationToken token)
    {
        if (_libraryRoot == null) return;

        // Sanitize path to prevent directory traversal
        var fullPath = Path.GetFullPath(Path.Combine(_libraryRoot, relativePath));
        if (!fullPath.StartsWith(Path.GetFullPath(_libraryRoot), StringComparison.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync("{\"error\":\"invalid path\"}");
            return;
        }

        if (!File.Exists(fullPath))
        {
            await writer.WriteLineAsync("{\"error\":\"not found\"}");
            return;
        }

        var info = new FileInfo(fullPath);
        var header = JsonSerializer.Serialize(new { size = info.Length });
        await writer.WriteLineAsync(header);
        await writer.FlushAsync(token);

        // Stream the file bytes
        await using var fileStream = File.OpenRead(fullPath);
        await fileStream.CopyToAsync(stream, token);

        _logger.LogInformation("PeerSync: Served {File} ({Size:N0} bytes)", relativePath, info.Length);
    }

    #endregion

    #region Sync Client

    private async Task SyncLoopAsync(CancellationToken token)
    {
        // Wait a bit before first sync to let discovery find peers
        await Task.Delay(TimeSpan.FromSeconds(15), token);

        while (!token.IsCancellationRequested)
        {
            try
            {
                var peers = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(3), token);
                foreach (var peer in peers)
                {
                    try
                    {
                        await SyncFromPeerAsync(peer, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "PeerSync: Failed to sync from {Peer}", peer.MachineName);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PeerSync: Sync loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), token);
        }
    }

    private async Task SyncFromPeerAsync(DiscoveredInstance peer, CancellationToken token)
    {
        // Get peer's manifest
        List<FileManifestEntry>? peerManifest;
        try
        {
            peerManifest = await GetPeerManifestAsync(peer.EndPoint.Address, token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PeerSync: Could not get manifest from {Peer}", peer.MachineName);
            return;
        }

        if (peerManifest == null || peerManifest.Count == 0) return;

        // Find files we don't have
        var missing = peerManifest
            .Where(f => !_manifest.ContainsKey(f.RelativePath) && !_synced.ContainsKey(f.RelativePath))
            .ToList();

        if (missing.Count == 0) return;

        _logger.LogInformation("PeerSync: {Count} new files available from {Peer}", missing.Count, peer.MachineName);

        foreach (var file in missing)
        {
            if (token.IsCancellationRequested) break;

            try
            {
                await DownloadFileFromPeerAsync(peer.EndPoint.Address, file, token);
                _synced[file.RelativePath] = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PeerSync: Failed to download {File} from {Peer}",
                    file.RelativePath, peer.MachineName);
            }
        }
    }

    private async Task<List<FileManifestEntry>?> GetPeerManifestAsync(IPAddress address, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(address, SyncPort, token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync("{\"type\":\"list\"}");
        var response = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(response)) return null;

        return JsonSerializer.Deserialize<List<FileManifestEntry>>(response);
    }

    private async Task DownloadFileFromPeerAsync(IPAddress address, FileManifestEntry file, CancellationToken token)
    {
        if (_libraryRoot == null) return;

        using var client = new TcpClient();
        await client.ConnectAsync(address, SyncPort, token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var request = JsonSerializer.Serialize(new SyncMessage { Type = "get", Path = file.RelativePath });
        await writer.WriteLineAsync(request);

        // Read header with file size
        var headerLine = await reader.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(headerLine)) return;

        using var headerDoc = JsonDocument.Parse(headerLine);
        if (headerDoc.RootElement.TryGetProperty("error", out var errorProp))
        {
            _logger.LogWarning("PeerSync: Peer error for {File}: {Error}", file.RelativePath, errorProp.GetString());
            return;
        }

        var size = headerDoc.RootElement.GetProperty("size").GetInt64();

        // Save to the source/ingress path so soulman re-organizes it (or directly to library)
        var destPath = Path.Combine(_libraryRoot, file.RelativePath);
        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        if (File.Exists(destPath))
        {
            _logger.LogDebug("PeerSync: File already exists, skipping: {File}", file.RelativePath);
            return;
        }

        var tempPath = destPath + ".soulman-tmp";
        try
        {
            await using (var fileStream = File.Create(tempPath))
            {
                var buffer = new byte[81920];
                long received = 0;
                while (received < size)
                {
                    var toRead = (int)Math.Min(buffer.Length, size - received);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), token);
                    if (read == 0) break;
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
                    received += read;
                }
            }

            File.Move(tempPath, destPath, overwrite: false);
            _manifest[file.RelativePath] = file;
            _logger.LogInformation("PeerSync: Downloaded {File} ({Size:N0} bytes) from peer", file.RelativePath, size);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    #endregion
}

public class SyncMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("path")]
    public string? Path { get; set; }
}

public record FileManifestEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("relativePath")] string RelativePath,
    [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size);
