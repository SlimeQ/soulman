namespace Soulman;

public sealed class PurgeService
{
    private readonly ILogger<PurgeService> _logger;

    public PurgeService(ILogger<PurgeService> logger)
    {
        _logger = logger;
    }

    public int ApplyPurges(SoulmanSettings settings)
    {
        var purgedPaths = PurgePathPolicy.GetSafeConfiguredPaths(settings, _logger);
        if (purgedPaths.Count == 0)
            return 0;

        var purgedCount = 0;

        foreach (var purged in purgedPaths)
        {
            var localPath = ResolveLocalPath(settings, purged);

            try
            {
                if (Directory.Exists(localPath))
                {
                    Directory.Delete(localPath, recursive: true);
                    purgedCount++;
                    _logger.LogInformation("Purged directory {Path}", localPath);
                    continue;
                }

                if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                    purgedCount++;
                    _logger.LogInformation("Purged file {Path}", localPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to purge configured path {Path}", localPath);
            }
        }

        return purgedCount;
    }

    private static string ResolveLocalPath(SoulmanSettings settings, string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/').TrimStart('/');
        var slash = normalized.IndexOf('/');
        if (slash > 0)
        {
            var prefix = normalized[..slash];
            var rest = normalized[(slash + 1)..];

            if (string.Equals(prefix, "Music", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.DestinationPath))
                return Path.Combine(settings.DestinationPath!, rest);

            if (string.Equals(prefix, "Movies", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.MovieDestinationPath))
                return Path.Combine(settings.MovieDestinationPath!, rest);

            if (string.Equals(prefix, "TV", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(settings.TvDestinationPath))
                return Path.Combine(settings.TvDestinationPath!, rest);
        }

        var basePath = settings.DestinationPath
                       ?? settings.MovieDestinationPath
                       ?? settings.TvDestinationPath
                       ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(basePath, normalized);
    }
}
