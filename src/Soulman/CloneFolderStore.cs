using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soulman;

public class CloneFolderStore
{
    private readonly ILogger<CloneFolderStore> _logger;
    private readonly string _path;
    private readonly object _sync = new();
    private CloneFolderData _data = new();

    public CloneFolderStore(ILogger<CloneFolderStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Soulman",
            "clonefolders.json");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Load();
    }

    public IReadOnlyCollection<string> GetFolders()
    {
        lock (_sync)
        {
            return new ReadOnlyCollection<string>(_data.Folders);
        }
    }

    public bool AddFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var full = Path.GetFullPath(folder);
        lock (_sync)
        {
            if (_data.Folders.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            _data.Folders.Add(full);
            Save();
            _logger.LogInformation("Added clone folder {Folder}", full);
            return true;
        }
    }

    public bool RemoveFolder(string folder)
    {
        var full = Path.GetFullPath(folder);
        lock (_sync)
        {
            var removed = _data.Folders.RemoveAll(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
                _logger.LogInformation("Removed clone folder {Folder}", full);
            }

            return removed;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _data.Folders.Clear();
            Save();
            _logger.LogInformation("Cleared clone folders");
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var data = JsonSerializer.Deserialize<CloneFolderData>(json);
                if (data != null)
                {
                    _data = data;
                    return;
                }
            }

            _data = new CloneFolderData();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load clone folder list, starting empty");
            _data = new CloneFolderData();
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
            var json = JsonSerializer.Serialize(_data, options);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save clone folder list to {Path}", _path);
        }
    }

    private class CloneFolderData
    {
        public List<string> Folders { get; set; } = new();
    }
}
