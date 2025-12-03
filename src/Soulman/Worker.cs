using Microsoft.Extensions.Options;

namespace Soulman;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;
    private readonly DownloadScanner _scanner;
    private readonly CloneFolderStore _cloneStore;
    private readonly PathPreferenceStore _pathStore;
    private readonly MoveNotificationBroker _moveBroker;

    public Worker(
        ILogger<Worker> logger,
        IOptionsMonitor<SoulmanSettings> options,
        DownloadScanner scanner,
        CloneFolderStore cloneStore,
        PathPreferenceStore pathStore,
        MoveNotificationBroker moveBroker)
    {
        _logger = logger;
        _options = options;
        _scanner = scanner;
        _cloneStore = cloneStore;
        _pathStore = pathStore;
        _moveBroker = moveBroker;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Soulman starting up");
        LogSettings(_options.CurrentValue);

        while (!stoppingToken.IsCancellationRequested)
        {
            var effective = BuildEffectiveSettings();
            var clones = _cloneStore.GetFolders();

            try
            {
                var moved = await _scanner.ScanAsync(effective, clones, stoppingToken);
                if (moved > 0)
                {
                    _moveBroker.Publish(moved, effective.DestinationPath ?? "<unset>");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan failed");
            }

            try
            {
                await Task.Delay(effective.PollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Soulman stopping");
    }

    private void LogSettings(SoulmanSettings settings)
    {
        _logger.LogInformation("Watching {Source} -> {Destination}; poll {PollSeconds}s, settle {SettleSeconds}s",
            settings.SourcePath ?? "<unset>",
            settings.DestinationPath ?? "<unset>",
            settings.PollInterval.TotalSeconds,
            settings.SettledWindow.TotalSeconds);
    }

    private SoulmanSettings BuildEffectiveSettings()
    {
        var baseSettings = _options.CurrentValue;
        var prefs = _pathStore.Get();

        return new SoulmanSettings
        {
            SourcePath = prefs.SourcePath ?? baseSettings.SourcePath,
            DestinationPath = prefs.DestinationPath ?? baseSettings.DestinationPath,
            AdditionalSources = new List<string>(baseSettings.AdditionalSources ?? new List<string>()),
            AllowedExtensions = baseSettings.AllowedExtensions ?? Array.Empty<string>(),
            PollIntervalSeconds = baseSettings.PollIntervalSeconds,
            SettledSeconds = baseSettings.SettledSeconds
        };
    }
}
