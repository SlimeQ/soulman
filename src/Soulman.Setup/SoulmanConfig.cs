using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoulmanSetup;

public class SoulmanConfig
{
    // Node Identity
    public string NodeName { get; set; } = System.Net.Dns.GetHostName();
    public int DiscoveryPort { get; set; } = 45832;

    // Node Behavior
    public bool DownloadFromSoulseek { get; set; } = false;
    public bool ReceiveFromPeers { get; set; } = true;

    // Soulseek (Only if DownloadFromSoulseek is true)
    public string? SoulseekUsername { get; set; }
    public string? SoulseekPassword { get; set; }
    public string? SoulseekDownloadFolder { get; set; }

    // Media Gathering Rules
    public bool GatherMusic { get; set; } = true;
    public bool GatherMovies { get; set; } = true;
    public bool GatherTV { get; set; } = true;

    // Library Paths
    public string? MusicLibraryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
    public string? MoviesLibraryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos/Movies");
    public string? TvLibraryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos/TV");

    // Legacy/Core Alignment
    public string? SourcePath { get; set; }
}
