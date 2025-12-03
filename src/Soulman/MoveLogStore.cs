using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulman;

public class MoveLogStore
{
    private readonly ILogger<MoveLogStore> _logger;
    private readonly string _path;
    private readonly object _sync = new();
    private readonly TimeSpan _retention = TimeSpan.FromHours(24);
    private List<MoveEntry> _entries = new();

    public MoveLogStore(ILogger<MoveLogStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soulman",
            "movelog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Load();
        EnsureFile();
    }

    public string LogFilePath => _path;

    public void Add(MoveEntry entry)
    {
        lock (_sync)
        {
            var cutoff = DateTimeOffset.UtcNow - _retention;
            _entries = _entries.Where(e => e.Timestamp >= cutoff).ToList();
            _entries.Add(entry);
            Save();
        }
    }

    public IReadOnlyList<MoveEntry> GetRecentEntries()
    {
        lock (_sync)
        {
            var cutoff = DateTimeOffset.UtcNow - _retention;
            _entries = _entries.Where(e => e.Timestamp >= cutoff).ToList();
            return _entries.ToList();
        }
    }

    public string EnsureLogFile()
    {
        lock (_sync)
        {
            EnsureFile();
            return _path;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<List<MoveEntry>>(json);
                if (loaded != null)
                {
                    var cutoff = DateTimeOffset.UtcNow - _retention;
                    _entries = loaded.Where(e => e.Timestamp >= cutoff).ToList();
                    return;
                }
            }

            _entries = new List<MoveEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load move log; starting fresh");
            _entries = new List<MoveEntry>();
        }
    }

    private void EnsureFile()
    {
        if (!File.Exists(_path))
        {
            Save();
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
            var json = JsonSerializer.Serialize(_entries, options);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save move log to {Path}", _path);
        }
    }
}

public record MoveEntry(
    DateTimeOffset Timestamp,
    string SourcePath,
    string DestinationPath,
    IReadOnlyCollection<string> CloneDestinations);
