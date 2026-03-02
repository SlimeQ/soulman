using System.IO;

namespace Soulman;

internal static class PurgePathPolicy
{
    public static IReadOnlyList<string> GetSafeConfiguredPaths(SoulmanSettings settings, ILogger logger)
    {
        var configured = settings.PurgedPaths ?? Array.Empty<string>();
        var safe = new List<string>();

        foreach (var raw in configured)
        {
            var normalized = Normalize(raw);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (!IsSafeScopedPath(normalized))
            {
                logger.LogWarning("Ignoring unsafe purge path {Path}. Purge paths must be scoped like Music/something, Movies/something, or TV/something.", raw);
                continue;
            }

            safe.Add(normalized);
        }

        return safe
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsPurgedPath(string relativePath, IReadOnlyList<string> purgedPaths)
    {
        var normalized = Normalize(relativePath);
        if (string.IsNullOrWhiteSpace(normalized) || purgedPaths.Count == 0)
            return false;

        foreach (var purged in purgedPaths)
        {
            if (normalized.Equals(purged, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(purged + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.Replace('\\', '/').Trim().Trim('/');
    }

    private static bool IsSafeScopedPath(string normalized)
    {
        var slash = normalized.IndexOf('/');
        if (slash <= 0 || slash == normalized.Length - 1)
            return false;

        var root = normalized[..slash];
        if (!root.Equals("Music", StringComparison.OrdinalIgnoreCase)
            && !root.Equals("Movies", StringComparison.OrdinalIgnoreCase)
            && !root.Equals("TV", StringComparison.OrdinalIgnoreCase))
            return false;

        var child = normalized[(slash + 1)..];
        if (string.IsNullOrWhiteSpace(child) || child == "." || child == "..")
            return false;

        return true;
    }
}
