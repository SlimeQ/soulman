using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TagLib;

namespace Soulman;

public class DownloadScanner
{
    private readonly ILogger<DownloadScanner> _logger;
    private readonly MoveLogStore _moveLog;
    private readonly ConcurrentDictionary<string, FileObservation> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _protectedPathWarnings = new(StringComparer.OrdinalIgnoreCase);

    // Heuristics
    private static readonly Regex TvSeasonEpisode = new(@"(.*?)[ .]S(\d{1,2})E(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MovieYear = new(@"(.*?)[ .]\(?(\d{4})\)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DownloadScanner(ILogger<DownloadScanner> logger, MoveLogStore moveLog)
    {
        _logger = logger;
        _moveLog = moveLog;
    }

    public async Task<int> ScanAsync(SoulmanSettings settings, CancellationToken token)
    {
        if (!ValidateSettings(settings))
        {
            return 0;
        }

        // Determine protected roots (destinations)
        var protectedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (settings.MusicLibraryPath != null) protectedRoots.Add(Path.GetFullPath(settings.MusicLibraryPath));
        if (settings.MoviesLibraryPath != null) protectedRoots.Add(Path.GetFullPath(settings.MoviesLibraryPath));
        if (settings.TvLibraryPath != null) protectedRoots.Add(Path.GetFullPath(settings.TvLibraryPath));

        var sources = GatherSources(settings).ToArray();

        if (sources.Length == 0)
        {
            _logger.LogWarning("No source folders to scan");
            return 0;
        }

        // Filter sources that might overlap with destinations
        var allowedSources = sources
            .Where(s =>
            {
                foreach (var dest in protectedRoots)
                {
                    if (IsSubPath(dest, s))
                    {
                        _logger.LogWarning("Destination {Destination} sits under source {Source}; skipping to avoid loops", dest, s);
                        return false;
                    }
                    if (IsSubPath(s, dest))
                    {
                        _logger.LogWarning("Source {Source} sits under destination {Destination}; skipping to avoid moving library files", s, dest);
                        return false;
                    }
                }
                return true;
            })
            .ToArray();

        if (allowedSources.Length == 0)
        {
            return 0;
        }

        var files = new List<string>();
        foreach (var source in allowedSources)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
                    .Where(settings.IsSupportedFile)
                    .Where(f =>
                    {
                        if (IsProtectedPath(f, protectedRoots, out var protectedRoot))
                        {
                            if (_protectedPathWarnings.Add(protectedRoot))
                            {
                                _logger.LogWarning("Skipping files under protected path {ProtectedPath}", protectedRoot);
                            }
                            return false;
                        }
                        return true;
                    }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate source {Source}", source);
            }
        }

        if (files.Count == 0) return 0;

        // Cleanup observed cache
        var now = DateTimeOffset.UtcNow;
        var existing = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        foreach (var tracked in _observed.Keys)
        {
            if (!existing.Contains(tracked))
            {
                _observed.TryRemove(tracked, out _);
            }
        }

        var movedCount = 0;
        foreach (var file in files)
        {
            if (token.IsCancellationRequested) break;

            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists) continue;
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

            if (await ProcessStableFileAsync(info, settings, token))
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

    private async Task<bool> ProcessStableFileAsync(FileInfo info, SoulmanSettings settings, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;

        try
        {
            string? targetPath = null;

            if (IsMusic(info.Extension))
            {
                if (settings.GatherMusic && !string.IsNullOrWhiteSpace(settings.MusicLibraryPath))
                {
                    var metadata = ReadTags(info);
                    targetPath = BuildMusicPath(settings.MusicLibraryPath, metadata, info);
                }
            }
            else if (IsVideo(info.Extension))
            {
                // Heuristic: TV vs Movie
                if (TryGetTvInfo(info.Name, out var show, out var season, out var episode))
                {
                    if (settings.GatherTV && !string.IsNullOrWhiteSpace(settings.TvLibraryPath))
                    {
                        targetPath = BuildTvPath(settings.TvLibraryPath, show, season, episode, info);
                    }
                }
                else
                {
                    // Assume Movie
                    if (settings.GatherMovies && !string.IsNullOrWhiteSpace(settings.MoviesLibraryPath))
                    {
                        TryGetMovieInfo(info.Name, out var title, out var year);
                        targetPath = BuildMoviePath(settings.MoviesLibraryPath, title, year, info);
                    }
                }
            }
            else if (IsSubtitle(info.Extension))
            {
                // Subtitle sidecar logic: try to put it where the video would go
                // This mimics the video logic but for subs
                if (TryGetTvInfo(info.Name, out var show, out var season, out var episode))
                {
                     if (settings.GatherTV && !string.IsNullOrWhiteSpace(settings.TvLibraryPath))
                    {
                        targetPath = BuildTvPath(settings.TvLibraryPath, show, season, episode, info);
                    }
                }
                else
                {
                     if (settings.GatherMovies && !string.IsNullOrWhiteSpace(settings.MoviesLibraryPath))
                    {
                        TryGetMovieInfo(info.Name, out var title, out var year);
                        targetPath = BuildMoviePath(settings.MoviesLibraryPath, title, year, info);
                    }
                }
            }

            if (string.IsNullOrEmpty(targetPath)) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var finalPath = EnsureUniquePath(targetPath);
            var originalPath = info.FullName;
            
            System.IO.File.Move(originalPath, finalPath);
            _logger.LogInformation("Moved {Source} -> {Destination}", originalPath, finalPath);
            
            // No clone logic anymore
            
            _moveLog.Add(new MoveEntry(DateTimeOffset.UtcNow, originalPath, finalPath, Array.Empty<string>()));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure while moving {File}", info.FullName);
        }

        await Task.CompletedTask;
        return false;
    }

    private static bool IsMusic(string ext) => 
        new[] { ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".aiff", ".alac", ".opus", ".wv", ".ape" }
        .Contains(ext, StringComparer.OrdinalIgnoreCase);

    private static bool IsVideo(string ext) =>
        new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm" }
        .Contains(ext, StringComparer.OrdinalIgnoreCase);

    private static bool IsSubtitle(string ext) =>
        new[] { ".srt", ".ass", ".sub", ".ssa", ".vtt" }
        .Contains(ext, StringComparer.OrdinalIgnoreCase);

    private static bool TryGetTvInfo(string filename, out string show, out int season, out int episode)
    {
        var match = TvSeasonEpisode.Match(filename);
        if (match.Success)
        {
            show = match.Groups[1].Value.Replace(".", " ").Trim();
            season = int.Parse(match.Groups[2].Value);
            episode = int.Parse(match.Groups[3].Value);
            return true;
        }
        show = "";
        season = 0;
        episode = 0;
        return false;
    }

    private static void TryGetMovieInfo(string filename, out string title, out string year)
    {
        var match = MovieYear.Match(filename);
        if (match.Success)
        {
            title = match.Groups[1].Value.Replace(".", " ").Trim();
            year = match.Groups[2].Value;
        }
        else
        {
            title = Path.GetFileNameWithoutExtension(filename).Replace(".", " ").Trim();
            year = "";
        }
    }

    private static string BuildTvPath(string root, string show, int season, int episode, FileInfo info)
    {
        var showClean = SanitizePathSegment(show);
        var seasonFolder = $"Season {season:00}";
        var fileName = $"{showClean} S{season:00}E{episode:00}{info.Extension}";
        return Path.Combine(root, showClean, seasonFolder, fileName);
    }

    private static string BuildMoviePath(string root, string title, string year, FileInfo info)
    {
        var titleClean = SanitizePathSegment(title);
        var folder = string.IsNullOrEmpty(year) ? titleClean : $"{titleClean} ({year})";
        return Path.Combine(root, folder, $"{folder}{info.Extension}");
    }

    // Existing Music Logic
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
            var discCount = tag.DiscCount > 0 ? (int?)tag.DiscCount : null;

            return new TrackMetadata(artist, album, title, track, disc, discCount);
        }
        catch
        {
            return new TrackMetadata(unknownArtist, unknownAlbum, fallbackTitle, null, null, null);
        }
    }

    private static string BuildMusicPath(string destinationRoot, TrackMetadata metadata, FileInfo info)
    {
        var artist = SanitizePathSegment(metadata.Artist);
        var album = SanitizePathSegment(metadata.Album);
        var title = SanitizePathSegment(metadata.Title);

        if (metadata.DiscNumber.HasValue && metadata.DiscNumber.Value > 0 &&
            (metadata.DiscNumber.Value > 1 || (metadata.DiscCount.HasValue && metadata.DiscCount.Value > 1)))
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

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? TakeFirstToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var separators = new[] { ';', ',', '/' };
        var idx = value.IndexOfAny(separators);
        return (idx > 0 ? value[..idx] : value).Trim();
    }

    private static string EnsureUniquePath(string path)
    {
        if (!System.IO.File.Exists(path)) return path;

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

    private static bool IsProtectedPath(string path, IReadOnlyCollection<string> protectedRoots, out string protectedRoot)
    {
        foreach (var root in protectedRoots)
        {
            if (IsSubPath(path, root))
            {
                protectedRoot = root;
                return true;
            }
        }
        protectedRoot = string.Empty;
        return false;
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
        return !string.IsNullOrWhiteSpace(settings.SourcePath) || (settings.AdditionalSources?.Any() ?? false);
    }

    private record FileObservation(long Length, DateTimeOffset LastSeen);
    private record TrackMetadata(string Artist, string Album, string Title, int? TrackNumber, int? DiscNumber, int? DiscCount);
}
