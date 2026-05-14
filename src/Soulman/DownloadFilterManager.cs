using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Soulman;

public class DownloadFilterManager
{
    private readonly ILogger<DownloadFilterManager> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;

    public DownloadFilterManager(
        ILogger<DownloadFilterManager> logger,
        IOptionsMonitor<SoulmanSettings> options)
    {
        _logger = logger;
        _options = options;
    }

    public DownloadFilterSettings GetFilters()
    {
        return DownloadFilterPolicy.Clone(_options.CurrentValue.DownloadFilters);
    }

    public IReadOnlyList<string> ListBlockedFolders()
    {
        var settings = _options.CurrentValue;
        return DownloadFilterPolicy.GetSafeBlockedFolders(settings.DownloadFilters, _logger);
    }

    public IReadOnlyList<string> ListBlockedPeers()
    {
        var settings = _options.CurrentValue;
        return DownloadFilterPolicy.GetNormalizedBlockedPeers(settings.DownloadFilters);
    }

    public (bool Success, string Message) SetCategoryAllowed(string category, bool allowed)
    {
        var normalized = NormalizeCategory(category);
        if (normalized == null)
        {
            return (false, "Unknown category. Expected Music, Movies, or TV.");
        }

        var next = DownloadFilterPolicy.Clone(_options.CurrentValue.DownloadFilters);
        switch (normalized)
        {
            case "Music":
                next.AllowMusic = allowed;
                break;
            case "Movies":
                next.AllowMovies = allowed;
                break;
            case "TV":
                next.AllowTv = allowed;
                break;
        }

        var saved = SaveSettings(next);
        if (!saved)
        {
            return (false, "Failed to save download filters.");
        }

        var state = allowed ? "enabled" : "disabled";
        _logger.LogInformation("Download category {Category} set to {State}", normalized, state);
        return (true, $"{normalized} downloads {state}.");
    }

    public (bool Success, bool IsBlocked, string Message) TogglePeerBlocked(string peerNameOrIp)
    {
        var normalized = DownloadFilterPolicy.NormalizePeer(peerNameOrIp);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return (false, false, "Peer name cannot be empty.");
        }

        var filters = DownloadFilterPolicy.Clone(_options.CurrentValue.DownloadFilters);
        var peers = DownloadFilterPolicy.GetNormalizedBlockedPeers(filters).ToList();
        var existing = peers.FirstOrDefault(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));

        bool isBlocked;
        string message;
        if (existing != null)
        {
            peers.Remove(existing);
            isBlocked = false;
            message = $"Downloads from '{normalized}' are now allowed.";
        }
        else
        {
            peers.Add(normalized);
            isBlocked = true;
            message = $"Downloads from '{normalized}' are now blocked.";
        }

        filters.BlockedPeers = peers
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!SaveSettings(filters))
        {
            return (false, false, "Failed to save blocked peers.");
        }

        _logger.LogInformation("Peer download block toggle for {Peer}: blocked={IsBlocked}", normalized, isBlocked);
        return (true, isBlocked, message);
    }

    public (bool Success, string Message) ClearBlockedPeers()
    {
        var filters = DownloadFilterPolicy.Clone(_options.CurrentValue.DownloadFilters);
        var count = DownloadFilterPolicy.GetNormalizedBlockedPeers(filters).Count;
        if (count == 0)
        {
            return (false, "No blocked peers to clear.");
        }

        filters.BlockedPeers = Array.Empty<string>();
        if (!SaveSettings(filters))
        {
            return (false, "Failed to save blocked peers.");
        }

        _logger.LogInformation("Cleared {Count} blocked peers", count);
        return (true, $"Cleared {count} blocked peer{(count == 1 ? string.Empty : "s")}.");
    }

    public (bool Success, string Message) AddFolderBlock(string path)
    {
        var normalized = DownloadFilterPolicy.NormalizePath(path);
        var validation = DownloadFilterPolicy.ValidateBlockedFolder(normalized);
        if (!validation.IsValid)
        {
            return (false, validation.Reason ?? "Invalid folder block.");
        }

        var filters = DownloadFilterPolicy.Clone(_options.CurrentValue.DownloadFilters);
        var current = DownloadFilterPolicy.GetSafeBlockedFolders(filters, _logger).ToList();
        if (current.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return (false, $"'{normalized}' is already blocked.");
        }

        current.Add(normalized);
        filters.BlockedFolders = current
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!SaveSettings(filters))
        {
            return (false, "Failed to save blocked folders.");
        }

        _logger.LogInformation("Added blocked folder {Path}", normalized);
        return (true, $"Added '{normalized}' to blocked folders.");
    }

    public (bool Success, string Message) RemoveFolderBlock(string path)
    {
        var normalized = DownloadFilterPolicy.NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return (false, "Path cannot be empty.");
        }

        var filters = DownloadFilterPolicy.Clone(_options.CurrentValue.DownloadFilters);
        var current = DownloadFilterPolicy.GetSafeBlockedFolders(filters, _logger).ToList();
        var existing = current.FirstOrDefault(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return (false, $"'{normalized}' is not blocked.");
        }

        current.Remove(existing);
        filters.BlockedFolders = current.ToArray();
        if (!SaveSettings(filters))
        {
            return (false, "Failed to save blocked folders.");
        }

        _logger.LogInformation("Removed blocked folder {Path}", normalized);
        return (true, $"Removed '{normalized}' from blocked folders.");
    }

    public (bool Success, string Message) ClearFolderBlocks()
    {
        var filters = DownloadFilterPolicy.Clone(_options.CurrentValue.DownloadFilters);
        var count = DownloadFilterPolicy.GetSafeBlockedFolders(filters, _logger).Count;
        if (count == 0)
        {
            return (false, "No blocked folders to clear.");
        }

        filters.BlockedFolders = Array.Empty<string>();
        if (!SaveSettings(filters))
        {
            return (false, "Failed to save blocked folders.");
        }

        _logger.LogInformation("Cleared {Count} blocked folders", count);
        return (true, $"Cleared {count} blocked folder{(count == 1 ? string.Empty : "s")}.");
    }

    private bool SaveSettings(DownloadFilterSettings filters)
    {
        try
        {
            var current = _options.CurrentValue;
            var configPath = GetConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            var settings = new SoulmanSettings
            {
                SourcePath = current.SourcePath,
                DestinationPath = current.DestinationPath,
                MovieDestinationPath = current.MovieDestinationPath,
                TvDestinationPath = current.TvDestinationPath,
                AdditionalSources = current.AdditionalSources,
                PollIntervalSeconds = current.PollIntervalSeconds,
                SettledSeconds = current.SettledSeconds,
                PurgedPaths = current.PurgedPaths,
                DownloadFilters = DownloadFilterPolicy.Clone(filters),
                AllowedExtensions = current.AllowedExtensions,
                VideoExtensions = current.VideoExtensions,
                SubtitleExtensions = current.SubtitleExtensions
            };

            var payload = JsonSerializer.Serialize(new { Soulman = settings }, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(configPath, payload);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save download filter settings.");
            return false;
        }
    }

    private static string? NormalizeCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        return category.Trim().ToUpperInvariant() switch
        {
            "MUSIC" => "Music",
            "MOVIES" => "Movies",
            "TV" => "TV",
            _ => null
        };
    }

    private static string GetConfigPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Soulman");
            return Path.Combine(configDir, "appsettings.json");
        }

        var nonWindowsConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "soulman");
        return Path.Combine(nonWindowsConfigDir, "appsettings.json");
    }
}
