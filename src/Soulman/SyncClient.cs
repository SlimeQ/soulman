using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Soulman;

public class SyncClient
{
    private readonly ILogger<SyncClient> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;
    private readonly MoveLogStore _moveLog;
    private readonly MoveNotificationBroker _moveBroker;
    private readonly TransferProgressBroker _progressBroker;

    public SyncClient(
        ILogger<SyncClient> logger,
        IOptionsMonitor<SoulmanSettings> options,
        MoveLogStore moveLog,
        MoveNotificationBroker moveBroker,
        TransferProgressBroker progressBroker)
    {
        _logger = logger;
        _options = options;
        _moveLog = moveLog;
        _moveBroker = moveBroker;
        _progressBroker = progressBroker;
    }

    public async Task SyncWithPeerAsync(DiscoveredInstance peer, CancellationToken token)
    {
        if (peer.SyncPort <= 0) return;

        _logger.LogInformation("Starting sync with {Machine} at {Endpoint}:{Port}", peer.MachineName, peer.EndPoint.Address, peer.SyncPort);

        try
        {
            using var client = new TcpClient();
            // Set timeouts to prevent hanging forever on broken connections
            client.ReceiveTimeout = 30000; // 30 seconds
            client.SendTimeout = 30000;

            await client.ConnectAsync(peer.EndPoint.Address, peer.SyncPort, token);

            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            // 1. Request File List
            await writer.WriteLineAsync("LIST");
            var json = await reader.ReadLineAsync(token);

            if (string.IsNullOrEmpty(json))
            {
                _logger.LogWarning("Received empty file list from {Machine}", peer.MachineName);
                return;
            }

            var remoteFiles = JsonSerializer.Deserialize<List<RemoteFile>>(json);
            if (remoteFiles == null) return;

            _logger.LogInformation("Peer {Machine} has {Count} files", peer.MachineName, remoteFiles.Count);

            var destination = _options.CurrentValue.DestinationPath;
            if (string.IsNullOrEmpty(destination)) return;

            int syncedCount = 0;

            foreach (var file in remoteFiles)
            {
                if (token.IsCancellationRequested) break;

                var localPath = Path.Combine(destination, file.Path);
                if (File.Exists(localPath))
                {
                    // Simple check: skip if exists. 
                    // Future: Check size/date if we want to update modified files.
                    continue;
                }

                _logger.LogInformation("Downloading {Path} ({Size} bytes)", file.Path, file.Size);

                await writer.WriteLineAsync($"GET {file.Path}");
                var response = await reader.ReadLineAsync(token);
                
                if (response != null && response.StartsWith("OK"))
                {
                    var sizePart = response.Split(' ').Skip(1).FirstOrDefault();
                    if (long.TryParse(sizePart, out var size))
                    {
                        await DownloadFileAsync(stream, localPath, size, token);
                        _moveLog.Add(new MoveEntry(DateTimeOffset.UtcNow, $"Peer://{peer.MachineName}/{file.Path}", localPath, Array.Empty<string>()));
                        syncedCount++;
                    }
                }
                else
                {
                    _logger.LogWarning("Failed to initiate download for {Path}: {Response}", file.Path, response);
                }
            }
            
            await writer.WriteLineAsync("BYE");
            _logger.LogInformation("Sync with {Machine} complete. Downloaded {Count} files.", peer.MachineName, syncedCount);
            
            if (syncedCount > 0)
            {
                _moveBroker.Publish(syncedCount, destination);
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed with {Machine}", peer.MachineName);
        }
    }

    private async Task DownloadFileAsync(NetworkStream stream, string localPath, long size, CancellationToken token)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var fileName = Path.GetFileName(localPath);
        _progressBroker.Report(fileName, 0, size);

        // Download to temp file first
        var tempPath = localPath + ".tmp";
        try
        {
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // We can't use CopyToAsync because we need to read exactly 'size' bytes, 
                // and the stream stays open for further commands.
                // CopyToAsync would wait for the stream to close.
                
                var buffer = new byte[81920]; // 80KB buffer
                long remaining = size;
                long totalRead = 0;
                
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

                while (remaining > 0)
                {
                    // Reset timeout for each chunk
                    cts.CancelAfter(TimeSpan.FromSeconds(30));

                    var readSize = (int)Math.Min(remaining, buffer.Length);
                    
                    int read;
                    try 
                    {
                        read = await stream.ReadAsync(buffer, 0, readSize, cts.Token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new IOException("Read timed out");
                    }

                    if (read == 0) throw new IOException("Unexpected end of stream");
                    await fileStream.WriteAsync(buffer, 0, read, token);
                    remaining -= read;
                    totalRead += read;
                    
                    _progressBroker.Report(fileName, totalRead, size);
                }
            }

            File.Move(tempPath, localPath, overwrite: true);
            _progressBroker.ReportCompletion(fileName);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    private class RemoteFile
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
    }
}
