using System.Collections.Generic;

namespace Soulman;

public sealed class DownloadFilterSettings
{
    public bool AllowMusic { get; set; } = true;

    public bool AllowMovies { get; set; } = true;

    public bool AllowTv { get; set; } = true;

    public string[] BlockedPeers { get; set; } = Array.Empty<string>();

    public string[] BlockedFolders { get; set; } = Array.Empty<string>();
}

internal sealed record DownloadFilterSnapshot(
    bool AllowMusic,
    bool AllowMovies,
    bool AllowTv,
    IReadOnlySet<string> BlockedPeers,
    IReadOnlyList<string> BlockedFolders);

internal static class DownloadFilterPolicy
{
    public static DownloadFilterSnapshot GetSnapshot(SoulmanSettings settings, ILogger logger)
    {
        var filters = settings.DownloadFilters ?? new DownloadFilterSettings();
        var blockedFolders = GetSafeBlockedFolders(filters, logger);
        var blockedPeers = new HashSet<string>(GetNormalizedBlockedPeers(filters), StringComparer.OrdinalIgnoreCase);

        return new DownloadFilterSnapshot(
            filters.AllowMusic,
            filters.AllowMovies,
            filters.AllowTv,
            blockedPeers,
            blockedFolders);
    }

    public static IReadOnlyList<string> GetSafeBlockedFolders(DownloadFilterSettings? filters, ILogger logger)
    {
        var configured = filters?.BlockedFolders ?? Array.Empty<string>();
        var safe = new List<string>();

        foreach (var raw in configured)
        {
            var normalized = NormalizePath(raw);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!IsSafeScopedPath(normalized))
            {
                logger.LogWarning("Ignoring unsafe download filter folder {Path}. Entries must be scoped like Music/something, Movies/something, or TV/something.", raw);
                continue;
            }

            safe.Add(normalized);
        }

        return safe
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> GetNormalizedBlockedPeers(DownloadFilterSettings? filters)
    {
        var configured = filters?.BlockedPeers ?? Array.Empty<string>();

        return configured
            .Select(NormalizePeer)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsPeerBlocked(DiscoveredInstance peer, DownloadFilterSnapshot snapshot)
    {
        var normalizedMachine = NormalizePeer(peer.MachineName);
        if (!string.IsNullOrWhiteSpace(normalizedMachine)
            && snapshot.BlockedPeers.Contains(normalizedMachine))
        {
            return true;
        }

        var normalizedAddress = NormalizePeer(peer.EndPoint.Address.ToString());
        return !string.IsNullOrWhiteSpace(normalizedAddress)
               && snapshot.BlockedPeers.Contains(normalizedAddress);
    }

    public static bool IsDownloadBlocked(string remoteRelativePath, DownloadFilterSnapshot snapshot)
    {
        var normalized = NormalizePath(remoteRelativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (!IsRootAllowed(normalized, snapshot))
        {
            return true;
        }

        foreach (var folder in snapshot.BlockedFolders)
        {
            if (normalized.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static (bool IsValid, string? Reason) ValidateBlockedFolder(string candidate)
    {
        var normalized = NormalizePath(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return (false, "Path cannot be empty.");
        }

        if (!IsSafeScopedPath(normalized))
        {
            return (false, "Folder blocks must be scoped like Music/something, Movies/something, or TV/something.");
        }

        return (true, null);
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Replace('\\', '/').Trim().Trim('/');
    }

    public static string NormalizePeer(string? peer)
    {
        return string.IsNullOrWhiteSpace(peer) ? string.Empty : peer.Trim();
    }

    public static DownloadFilterSettings Clone(DownloadFilterSettings? source)
    {
        source ??= new DownloadFilterSettings();

        return new DownloadFilterSettings
        {
            AllowMusic = source.AllowMusic,
            AllowMovies = source.AllowMovies,
            AllowTv = source.AllowTv,
            BlockedPeers = GetNormalizedBlockedPeers(source).ToArray(),
            BlockedFolders = source.BlockedFolders?.ToArray() ?? Array.Empty<string>()
        };
    }

    private static bool IsRootAllowed(string normalizedPath, DownloadFilterSnapshot snapshot)
    {
        if (TryGetRoot(normalizedPath, out var root))
        {
            return root switch
            {
                "Music" => snapshot.AllowMusic,
                "Movies" => snapshot.AllowMovies,
                "TV" => snapshot.AllowTv,
                _ => true
            };
        }

        // Legacy/single-root peers may publish rootless paths. Treat those as Music.
        return snapshot.AllowMusic;
    }

    private static bool TryGetRoot(string normalizedPath, out string root)
    {
        root = string.Empty;
        var slash = normalizedPath.IndexOf('/');

        var head = slash > 0
            ? normalizedPath[..slash]
            : normalizedPath;

        if (head.Equals("Music", StringComparison.OrdinalIgnoreCase))
        {
            root = "Music";
            return true;
        }

        if (head.Equals("Movies", StringComparison.OrdinalIgnoreCase))
        {
            root = "Movies";
            return true;
        }

        if (head.Equals("TV", StringComparison.OrdinalIgnoreCase))
        {
            root = "TV";
            return true;
        }

        return false;
    }

    private static bool IsSafeScopedPath(string normalized)
    {
        var slash = normalized.IndexOf('/');
        if (slash <= 0 || slash == normalized.Length - 1)
        {
            return false;
        }

        var root = normalized[..slash];
        if (!root.Equals("Music", StringComparison.OrdinalIgnoreCase)
            && !root.Equals("Movies", StringComparison.OrdinalIgnoreCase)
            && !root.Equals("TV", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var child = normalized[(slash + 1)..];
        if (string.IsNullOrWhiteSpace(child) || child == "." || child == "..")
        {
            return false;
        }

        return true;
    }
}
