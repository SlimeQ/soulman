using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Soulman;

/// <summary>
/// Manages the PurgedPaths blacklist for both CLI and UI consumers.
/// Handles CRUD operations, validation via PurgePathPolicy, and persistence.
/// </summary>
public class BlacklistManager
{
    private readonly ILogger<BlacklistManager> _logger;
    private readonly IOptionsMonitor<SoulmanSettings> _options;

    public BlacklistManager(
        ILogger<BlacklistManager> logger,
        IOptionsMonitor<SoulmanSettings> options)
    {
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Lists all currently blacklisted paths.
    /// </summary>
    public IReadOnlyList<string> List()
    {
        return _options.CurrentValue.PurgedPaths?.ToList() ?? new List<string>();
    }

    /// <summary>
    /// Adds a path to the blacklist. Returns true if added, false if already exists or invalid.
    /// </summary>
    public (bool Success, string Message) Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, "Path cannot be empty.");
        }

        var normalized = NormalizePath(path);
        var validation = PurgePathPolicy.Validate(normalized);

        if (!validation.IsValid)
        {
            return (false, validation.Reason ?? "Invalid path.");
        }

        var current = _options.CurrentValue.PurgedPaths?.ToList() ?? new List<string>();

        if (current.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return (false, $"'{normalized}' is already blacklisted.");
        }

        current.Add(normalized);
        var saved = SaveSettings(current);

        if (saved)
        {
            _logger.LogInformation("Added '{Path}' to blacklist.", normalized);
            return (true, $"Added '{normalized}' to blacklist.");
        }

        return (false, "Failed to save settings.");
    }

    /// <summary>
    /// Removes a path from the blacklist. Returns true if removed, false if not found.
    /// </summary>
    public (bool Success, string Message) Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, "Path cannot be empty.");
        }

        var normalized = NormalizePath(path);
        var current = _options.CurrentValue.PurgedPaths?.ToList() ?? new List<string>();

        var existing = current.FirstOrDefault(p => 
            string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            return (false, $"'{normalized}' is not in the blacklist.");
        }

        current.Remove(existing);
        var saved = SaveSettings(current);

        if (saved)
        {
            _logger.LogInformation("Removed '{Path}' from blacklist.", normalized);
            return (true, $"Removed '{normalized}' from blacklist.");
        }

        return (false, "Failed to save settings.");
    }

    /// <summary>
    /// Clears all paths from the blacklist.
    /// </summary>
    public (bool Success, string Message) Clear()
    {
        var current = _options.CurrentValue.PurgedPaths?.ToList() ?? new List<string>();
        
        if (current.Count == 0)
        {
            return (false, "Blacklist is already empty.");
        }

        var count = current.Count;
        var saved = SaveSettings(new List<string>());

        if (saved)
        {
            _logger.LogInformation("Cleared blacklist ({Count} paths removed).", count);
            return (true, $"Cleared blacklist ({count} paths removed).");
        }

        return (false, "Failed to save settings.");
    }

    private static string NormalizePath(string path)
    {
        // Normalize to forward slashes, trim whitespace and trailing slashes
        return path.Trim()
            .Replace('\\', '/')
            .TrimEnd('/');
    }

    private bool SaveSettings(List<string> purgedPaths)
    {
        try
        {
            var configPath = GetConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

            var current = _options.CurrentValue;
            var settings = new SoulmanSettings
            {
                SourcePath = current.SourcePath,
                DestinationPath = current.DestinationPath,
                MovieDestinationPath = current.MovieDestinationPath,
                TvDestinationPath = current.TvDestinationPath,
                AdditionalSources = current.AdditionalSources,
                PollIntervalSeconds = current.PollIntervalSeconds,
                SettledSeconds = current.SettledSeconds,
                PurgedPaths = purgedPaths.ToArray(),
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
            _logger.LogError(ex, "Failed to save settings.");
            return false;
        }
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
        else
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "soulman");
            return Path.Combine(configDir, "appsettings.json");
        }
    }
}