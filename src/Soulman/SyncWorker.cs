using Microsoft.Extensions.Options;

namespace Soulman;

public class SyncWorker : BackgroundService
{
    private readonly ILogger<SyncWorker> _logger;
    private readonly InstanceDiscovery _discovery;
    private readonly SyncClient _client;
    private readonly IOptionsMonitor<SoulmanSettings> _options;

    public SyncWorker(
        ILogger<SyncWorker> logger,
        InstanceDiscovery discovery,
        SyncClient client,
        IOptionsMonitor<SoulmanSettings> options)
    {
        _logger = logger;
        _discovery = discovery;
        _client = client;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit for the app to start and network to settle
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting peer discovery for sync...");
                var peers = await _discovery.DiscoverAsync(TimeSpan.FromSeconds(3), stoppingToken);
                
                if (peers.Count == 0)
                {
                    _logger.LogInformation("No peers found.");
                }
                else
                {
                    _logger.LogInformation("Found {Count} peers. Starting sync.", peers.Count);
                    foreach (var peer in peers)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        try
                        {
                            await _client.SyncWithPeerAsync(peer, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Unhandled error while syncing with {Peer}", peer.MachineName);
                        }
                    }
                }

                // Wait for next cycle. Use a fixed interval for now, maybe config later.
                // Let's say 1 minute for testing, but realistically 5-10 mins.
                // Let's use 5 minutes as a default.
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SyncWorker loop");
                // Wait before retrying to avoid tight loop on error
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
