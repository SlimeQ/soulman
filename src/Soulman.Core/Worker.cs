using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Soulman;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;
    private readonly DownloadScanner _scanner;
    private readonly PathPreferenceStore _pathStore;
    private readonly MoveNotificationBroker _moveBroker;

    public Worker(
        ILogger<Worker> logger,
        IOptionsMonitor<SoulmanSettings> options,
        DownloadScanner scanner,
        PathPreferenceStore pathStore,
        MoveNotificationBroker moveBroker)
    {
        _logger = logger;
        _options = options;
        _scanner = scanner;
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

            try
            {
                var moved = await _scanner.ScanAsync(effective, stoppingToken);
                if (moved > 0)
                {
                    _moveBroker.Publish(moved, effective.MusicLibraryPath ?? "<unset>");
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
        _logger.LogInformation("Watching {Source} -> Music:{Music}, Movies:{Movies}, TV:{TV}; poll {PollSeconds}s, settle {SettleSeconds}s",
            settings.SourcePath ?? "<unset>",
            settings.MusicLibraryPath ?? "<unset>",
            settings.MoviesLibraryPath ?? "<unset>",
            settings.TvLibraryPath ?? "<unset>",
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
            // Map legacy/prefs DestinationPath to MusicLibraryPath
            MusicLibraryPath = prefs.DestinationPath ?? baseSettings.MusicLibraryPath, // MusicLibraryPath defaults to old Dest logic in Settings class
            
            MoviesLibraryPath = baseSettings.MoviesLibraryPath,
            TvLibraryPath = baseSettings.TvLibraryPath,
            
            GatherMusic = baseSettings.GatherMusic,
            GatherMovies = baseSettings.GatherMovies,
            GatherTV = baseSettings.GatherTV,
            
            DownloadFromSoulseek = baseSettings.DownloadFromSoulseek,
            ReceiveFromPeers = baseSettings.ReceiveFromPeers,
            KnownPeers = baseSettings.KnownPeers ?? new List<string>(),

            AdditionalSources = new List<string>(baseSettings.AdditionalSources ?? new List<string>()),
            AllowedExtensions = baseSettings.AllowedExtensions ?? Array.Empty<string>(),
            PollIntervalSeconds = baseSettings.PollIntervalSeconds,
            SettledSeconds = baseSettings.SettledSeconds,
            DiscoveryPort = baseSettings.DiscoveryPort
        };
    }
}
