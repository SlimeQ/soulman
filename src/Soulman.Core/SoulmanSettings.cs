using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Soulman;

public class SoulmanSettings
{
    // Node Mode Settings
    public bool DownloadFromSoulseek { get; set; } = true;
    public bool ReceiveFromPeers { get; set; } = true;
    public List<string> KnownPeers { get; set; } = new();

    // Soulseek Credentials (if acting as node)
    public string? SoulseekUsername { get; set; }
    public string? SoulseekPassword { get; set; }
    public string? SoulseekDownloadFolder { get; set; }

    // Media Types
    public bool GatherMusic { get; set; } = true;
    public bool GatherMovies { get; set; } = false;
    public bool GatherTV { get; set; } = false;

    // Library Paths
    public string? MusicLibraryPath { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public string? MoviesLibraryPath { get; set; }
    public string? TvLibraryPath { get; set; }

    [Obsolete("Use MusicLibraryPath")]
    public string? DestinationPath { get => MusicLibraryPath; set => MusicLibraryPath = value; }

    // Legacy / Compat
    public string? SourcePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Soulseek Downloads", "complete");

    public List<string> AdditionalSources { get; set; } = new();

    public int PollIntervalSeconds { get; set; } = 30;
    public int SettledSeconds { get; set; } = 20;
    public int DiscoveryPort { get; set; } = 55000;

    public string[] AllowedExtensions { get; set; } =
    {
        // Audio
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".aiff", ".alac", ".opus", ".wv", ".ape",
        // Video
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
        // Subtitles
        ".srt", ".ass", ".sub", ".ssa", ".vtt"
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
