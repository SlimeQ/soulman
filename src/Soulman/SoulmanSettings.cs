using System;
using System.IO;
using System.Linq;

namespace Soulman;

public class SoulmanSettings
{
    public string? SourcePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Soulseek Downloads", "complete");

    public string? DestinationPath { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

    public List<string> AdditionalSources { get; set; } = new();

    public int PollIntervalSeconds { get; set; } = 30;

    public int SettledSeconds { get; set; } = 20;

    public string[] AllowedExtensions { get; set; } =
    {
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".aiff", ".alac", ".opus", ".wv", ".ape"
    };

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(PollIntervalSeconds, 5));

    public TimeSpan SettledWindow => TimeSpan.FromSeconds(Math.Max(SettledSeconds, 5));

    public bool IsSupportedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || AllowedExtensions is null || AllowedExtensions.Length == 0)
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return AllowedExtensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }
}
