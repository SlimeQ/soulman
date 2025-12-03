using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulman;

public class PathPreferenceStore
{
    private readonly ILogger<PathPreferenceStore> _logger;
    private readonly string _path;
    private readonly object _sync = new();
    private PathPreferences _prefs = new();

    public PathPreferenceStore(ILogger<PathPreferenceStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Soulman",
            "paths.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Load();
    }

    public PathPreferences Get()
    {
        lock (_sync)
        {
            return _prefs with { };
        }
    }

    public void SetSource(string? path)
    {
        lock (_sync)
        {
            _prefs = _prefs with { SourcePath = Normalize(path) };
            Save();
        }
    }

    public void SetDestination(string? path)
    {
        lock (_sync)
        {
            _prefs = _prefs with { DestinationPath = Normalize(path) };
            Save();
        }
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetFullPath(path);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<PathPreferences>(json);
                if (loaded != null)
                {
                    _prefs = loaded;
                    return;
                }
            }

            _prefs = new PathPreferences();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load path preferences, using defaults");
            _prefs = new PathPreferences();
        }
    }

    private void Save()
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            var json = JsonSerializer.Serialize(_prefs, options);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save path preferences to {Path}", _path);
        }
    }
}

public record PathPreferences(string? SourcePath = null, string? DestinationPath = null);
