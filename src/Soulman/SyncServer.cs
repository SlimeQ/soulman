using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Soulman;

public class SyncServer : IHostedService, IDisposable
{
    private readonly ILogger<SyncServer> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;
    private TcpListener? _listener;
    private Task? _listeningTask;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; private set; }

    public SyncServer(ILogger<SyncServer> logger, IOptionsMonitor<SoulmanSettings> options)
    {
        _logger = logger;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Try fixed port first, then dynamic
        try
        {
            _listener = new TcpListener(IPAddress.Any, 45833);
            _listener.Start();
        }
        catch
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
        }

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        
        _logger.LogInformation("SyncServer started on port {Port}", Port);

        _listeningTask = AcceptClientsAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _listener?.Stop();
        if (_listeningTask != null)
        {
            try
            {
                await _listeningTask;
            }
            catch
            {
                // ignore
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _listener?.Stop();
    }

    private async Task AcceptClientsAsync(CancellationToken token)
    {
        if (_listener == null) return;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting sync client");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
        await using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
        {
            _logger.LogDebug("Client connected: {Remote}", client.Client.RemoteEndPoint);

            try
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line == null) break;

                    var parts = line.Split(' ', 2);
                    var command = parts[0].ToUpperInvariant();
                    var arg = parts.Length > 1 ? parts[1] : string.Empty;

                    switch (command)
                    {
                        case "LIST":
                            await HandleList(writer);
                            break;
                        case "GET":
                            await HandleGet(arg, writer, stream);
                            break;
                        case "BYE":
                            return;
                        default:
                            await writer.WriteLineAsync("ERROR Unknown command");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error handling client {Remote}", client.Client.RemoteEndPoint);
            }
        }
    }

    private async Task HandleList(StreamWriter writer)
    {
        var settings = _options.CurrentValue;
        var root = GetSyncRoot(settings);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            await writer.WriteLineAsync("[]");
            return;
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => settings.IsSupportedFile(f))
            .Select(f => new
            {
                Path = Path.GetRelativePath(root, f).Replace('\\', '/'),
                Size = new FileInfo(f).Length
            })
            .ToList();

        var json = JsonSerializer.Serialize(files);
        await writer.WriteLineAsync(json);
    }

    private async Task HandleGet(string relativePath, StreamWriter writer, NetworkStream stream)
    {
        // Security check: Prevent directory traversal
        // We rely primarily on the path anchoring check below, but we can do a quick check for explicit traversal sequences
        if (relativePath.Contains("../") || relativePath.Contains("..\\") || Path.IsPathRooted(relativePath))
        {
            await writer.WriteLineAsync("ERROR Invalid path");
            return;
        }

        var settings = _options.CurrentValue;
        var root = GetSyncRoot(settings);
        if (string.IsNullOrEmpty(root))
        {
             await writer.WriteLineAsync("ERROR No library configured");
             return;
        }

        var fullPath = Path.Combine(root, relativePath);
        // Ensure the full path is actually within the root
        if (!Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
             await writer.WriteLineAsync("ERROR Access denied");
             return;
        }

        if (!File.Exists(fullPath))
        {
            await writer.WriteLineAsync("ERROR File not found");
            return;
        }

        var info = new FileInfo(fullPath);
        var remoteLabel = stream.Socket?.RemoteEndPoint?.ToString() ?? "<unknown>";
        _logger.LogInformation("Serving {Path} ({Size} bytes) to {Remote}", relativePath, info.Length, remoteLabel);
        await writer.WriteLineAsync($"OK {info.Length}");
        
        // Important: Flush the writer buffer before writing raw bytes to the underlying stream
        await writer.FlushAsync();

        using var fileStream = File.OpenRead(fullPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await fileStream.CopyToAsync(stream);
            _logger.LogInformation("Finished serving {Path} in {Elapsed}ms", relativePath, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error serving {Path} to {Remote}", relativePath, remoteLabel);
            throw;
        }
    }

    private static string? GetSyncRoot(SoulmanSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SyncRootPath))
        {
            return settings.SyncRootPath;
        }

        return settings.DestinationPath;
    }
}
