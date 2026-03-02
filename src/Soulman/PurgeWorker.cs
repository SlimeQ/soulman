using Microsoft.Extensions.Options;

namespace Soulman;

public sealed class PurgeWorker : BackgroundService
{
    private readonly ILogger<PurgeWorker> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;
    private readonly PurgeService _purgeService;

    public PurgeWorker(ILogger<PurgeWorker> logger, IOptionsMonitor<SoulmanSettings> options, PurgeService purgeService)
    {
        _logger = logger;
        _options = options;
        _purgeService = purgeService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ApplyPurges(_options.CurrentValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PurgeWorker cycle failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    private void ApplyPurges(SoulmanSettings settings)
    {
        _purgeService.ApplyPurges(settings);
    }
}
