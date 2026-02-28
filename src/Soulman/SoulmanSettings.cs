using System;
using System.IO;
using System.Linq;

namespace Soulman;

public class SoulmanSettings
{
    public string? SourcePath { get; set; } = GetDefaultSourcePath();

    public string? DestinationPath { get; set; } = GetDefaultDestinationPath();

    public string? MovieDestinationPath { get; set; } = GetDefaultMoviePath();

    public string? TvDestinationPath { get; set; } = GetDefaultTvPath();

    // Optional root used for peer sync LIST/GET operations. If unset, falls back to
    // DestinationPath (legacy behavior). For multi-library setups, point this at a
    // common root (e.g. /srv/media-library) so Music/Movies/TV all sync.
    public string? SyncRootPath { get; set; }

    public List<string> AdditionalSources { get; set; } = new();

    public int PollIntervalSeconds { get; set; } = 30;

    public int SettledSeconds { get; set; } = 20;

    public string[] AllowedExtensions { get; set; } =
    {
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".aiff", ".alac", ".opus", ".wv", ".ape"
    };

    public string[] VideoExtensions { get; set; } =
    {
        ".mkv", ".mp4", ".avi", ".mov", ".webm", ".m4v", ".wmv", ".mpg", ".mpeg"
    };

    public string[] SubtitleExtensions { get; set; } =
    {
        ".srt", ".sub", ".ass", ".ssa", ".idx", ".vtt"
    };

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(PollIntervalSeconds, 5));

    public TimeSpan SettledWindow => TimeSpan.FromSeconds(Math.Max(SettledSeconds, 5));

    public bool IsAudioFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || AllowedExtensions is null || AllowedExtensions.Length == 0)
            return false;
        var ext = Path.GetExtension(path);
        return AllowedExtensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || VideoExtensions is null || VideoExtensions.Length == 0)
            return false;
        var ext = Path.GetExtension(path);
        return VideoExtensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsSubtitleFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || SubtitleExtensions is null || SubtitleExtensions.Length == 0)
            return false;
        var ext = Path.GetExtension(path);
        return SubtitleExtensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsSupportedFile(string path) =>
        IsAudioFile(path) || IsVideoFile(path) || IsSubtitleFile(path);

    private static string? GetDefaultSourcePath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Soulseek Downloads", "complete");
        }

        // Linux/macOS: ~/Downloads/SoulmanIngress
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "SoulmanIngress");
    }

    private static string? GetDefaultDestinationPath()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

        // Linux/macOS: ~/Music
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Music");
    }

    private static string? GetDefaultMoviePath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "Movies");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Videos", "Movies");
    }

    private static string? GetDefaultTvPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "TV");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Videos", "TV");
    }
}
