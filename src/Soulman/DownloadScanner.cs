using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TagLib;

namespace Soulman;

public class DownloadScanner
{
    private readonly ILogger<DownloadScanner> _logger;
    private readonly MoveLogStore _moveLog;
    private readonly ConcurrentDictionary<string, FileObservation> _observed = new(StringComparer.OrdinalIgnoreCase);

    public DownloadScanner(ILogger<DownloadScanner> logger, MoveLogStore moveLog)
    {
        _logger = logger;
        _moveLog = moveLog;
    }

    public async Task<int> ScanAsync(SoulmanSettings settings, IReadOnlyCollection<string> cloneDestinations,
        CancellationToken token)
    {
        if (!ValidateSettings(settings))
        {
            return 0;
        }

        var destination = Path.GetFullPath(settings.DestinationPath!);
        var sources = GatherSources(settings).ToArray();

        if (sources.Length == 0)
        {
            _logger.LogWarning("No source folders to scan");
            return 0;
        }

        var allowedSources = sources
            .Where(s =>
            {
                if (IsSubPath(destination, s))
                {
                    _logger.LogWarning("Destination {Destination} sits under source {Source}; skipping to avoid loops",
                        destination, s);
                    return false;
                }

                return true;
            })
            .ToArray();

        if (allowedSources.Length == 0)
        {
            _logger.LogWarning("No valid sources after filtering unsafe destinations");
            return 0;
        }

        var files = new List<string>();
        foreach (var source in allowedSources)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
                    .Where(settings.IsSupportedFile));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate source {Source}", source);
            }
        }

        if (files.Count == 0)
        {
            return 0;
        }

        var movedCount = 0;
        var now = DateTimeOffset.UtcNow;
        var existing = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var tracked in _observed.Keys)
        {
            if (!existing.Contains(tracked))
            {
                _observed.TryRemove(tracked, out _);
            }
        }

        foreach (var file in files)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping file {File} because it could not be read", file);
                continue;
            }

            var key = info.FullName;
            var size = info.Length;

            if (!_observed.TryGetValue(key, out var observation))
            {
                _observed[key] = new FileObservation(size, now);
                continue;
            }

            if (observation.Length != size)
            {
                _observed[key] = new FileObservation(size, now);
                continue;
            }

            if (now - observation.LastSeen < settings.SettledWindow)
            {
                continue;
            }

            if (await ProcessStableFileAsync(info, destination, cloneDestinations, token))
            {
                movedCount++;
            }
            _observed.TryRemove(key, out _);
        }

        return movedCount;
    }

    private IEnumerable<string> GatherSources(SoulmanSettings settings)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(settings.SourcePath) && Directory.Exists(settings.SourcePath))
        {
            set.Add(Path.GetFullPath(settings.SourcePath));
        }

        if (settings.AdditionalSources != null)
        {
            foreach (var path in settings.AdditionalSources)
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    set.Add(Path.GetFullPath(path));
                }
            }
        }

        return set;
    }

    private async Task<bool> ProcessStableFileAsync(FileInfo info, string destinationRoot,
        IReadOnlyCollection<string> cloneDestinations, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            var metadata = ReadTags(info);
            var targetPath = BuildDestinationPath(destinationRoot, metadata, info);

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            var finalPath = EnsureUniquePath(targetPath);
            var originalPath = info.FullName;
            System.IO.File.Move(originalPath, finalPath);
            _logger.LogInformation("Moved {Source} -> {Destination}", originalPath, finalPath);
            var clones = ReplicateClones(finalPath, destinationRoot, cloneDestinations);
            _moveLog.Add(new MoveEntry(DateTimeOffset.UtcNow, originalPath, finalPath, clones));
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO failure while moving {File}", info.FullName);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied while moving {File}", info.FullName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure while moving {File}", info.FullName);
        }

        await Task.CompletedTask;
        return false;
    }

    private static TrackMetadata ReadTags(FileInfo info)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(info.Name);
        const string unknownArtist = "Unknown Artist";
        const string unknownAlbum = "Unknown Album";

        try
        {
            using var tagFile = TagLib.File.Create(info.FullName);
            var tag = tagFile.Tag;
            var artist = ResolveAlbumArtist(tag) ?? unknownArtist;
            var album = string.IsNullOrWhiteSpace(tag.Album) ? unknownAlbum : tag.Album!;
            var title = string.IsNullOrWhiteSpace(tag.Title) ? fallbackTitle : tag.Title!;
            var track = tag.Track > 0 ? (int?)tag.Track : null;
            var disc = tag.Disc > 0 ? (int?)tag.Disc : null;

            return new TrackMetadata(artist, album, title, track, disc);
        }
        catch
        {
            return new TrackMetadata(unknownArtist, unknownAlbum, fallbackTitle, null, null);
        }
    }

    private static string BuildDestinationPath(string destinationRoot, TrackMetadata metadata, FileInfo info)
    {
        var artist = SanitizePathSegment(metadata.Artist);
        var album = SanitizePathSegment(metadata.Album);
        var title = SanitizePathSegment(metadata.Title);

        if (metadata.DiscNumber.HasValue && metadata.DiscNumber.Value > 0)
        {
            album = $"{album} (Disc {metadata.DiscNumber.Value})";
        }

        var prefix = metadata.TrackNumber.HasValue ? $"{metadata.TrackNumber.Value:00} - " : string.Empty;
        var fileName = $"{prefix}{title}{info.Extension}";

        return Path.Combine(destinationRoot, artist, album, fileName);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private IReadOnlyCollection<string> ReplicateClones(string primaryPath, string destinationRoot,
        IReadOnlyCollection<string> cloneDestinations)
    {
        if (cloneDestinations == null || cloneDestinations.Count == 0)
        {
            return Array.Empty<string>();
        }

        string? relative;
        try
        {
            relative = Path.GetRelativePath(destinationRoot, primaryPath);
        }
        catch
        {
            relative = Path.GetFileName(primaryPath);
        }

        var clonePaths = new List<string>();

        foreach (var clone in cloneDestinations)
        {
            if (string.IsNullOrWhiteSpace(clone))
            {
                continue;
            }

            try
            {
                var cloneRoot = Path.GetFullPath(clone);
                var clonePath = Path.Combine(cloneRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(clonePath)!);
                System.IO.File.Copy(primaryPath, clonePath, overwrite: true);
                _logger.LogInformation("Cloned {Source} -> {CloneDest}", primaryPath, clonePath);
                clonePaths.Add(clonePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clone {Source} to {CloneRoot}", primaryPath, clone);
            }
        }

        return clonePaths;
    }

    private static string? ResolveAlbumArtist(Tag tag)
    {
        const string compilations = "Various Artists";

        var albumArtists = tag.AlbumArtists ?? Array.Empty<string>();
        var joinedAlbumArtists = tag.JoinedAlbumArtists;

        var albumArtist = albumArtists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))
                          ?? FirstNonEmpty(tag.FirstAlbumArtist);

        var performer = tag.Performers?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
                        ?? FirstNonEmpty(tag.FirstPerformer);

        var candidate = FirstNonEmpty(albumArtist, performer);
        var hasMultipleAlbumArtists = albumArtists.Length > 1
                                      || (!string.IsNullOrWhiteSpace(joinedAlbumArtists)
                                          && joinedAlbumArtists.IndexOfAny(new[] { ',', ';', '/' }) >= 0);

        var normalized = TakeFirstToken(candidate);

        if (string.IsNullOrWhiteSpace(normalized) || hasMultipleAlbumArtists ||
            string.Equals(normalized, compilations, StringComparison.OrdinalIgnoreCase))
        {
            return compilations;
        }

        return normalized;
    }

    private static string? TakeFirstToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var separators = new[] { ';', ',', '/' };
        var idx = value.IndexOfAny(separators);
        return (idx > 0 ? value[..idx] : value).Trim();
    }

    private static string EnsureUniquePath(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var baseName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var counter = 1;

        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{baseName} ({counter}){extension}");
            counter++;
        } while (System.IO.File.Exists(candidate));

        return candidate;
    }

    private static bool IsSubPath(string candidate, string potentialParent)
    {
        var candidateFull = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentFull = Path.GetFullPath(potentialParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return candidateFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
    }

    private bool ValidateSettings(SoulmanSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DestinationPath))
        {
            _logger.LogWarning("Destination path is not configured.");
            return false;
        }

        var hasConfiguredSource = !string.IsNullOrWhiteSpace(settings.SourcePath)
                                  || (settings.AdditionalSources?.Any() ?? false);

        if (!hasConfiguredSource)
        {
            _logger.LogWarning("No source folders configured.");
            return false;
        }

        Directory.CreateDirectory(settings.DestinationPath);
        return true;
    }

    private record FileObservation(long Length, DateTimeOffset LastSeen);

    private record TrackMetadata(string Artist, string Album, string Title, int? TrackNumber, int? DiscNumber);
}
