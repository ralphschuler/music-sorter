using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicSorter.Core;

namespace MusicSorter.Metadata;

public sealed record TrackIndexSnapshot(
    int SchemaVersion, string Source, DateTimeOffset SavedAtUtc,
    IReadOnlyList<Track> Tracks, IReadOnlyDictionary<string, AudioFingerprint> Fingerprints);

public static class TrackIndex
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task SaveAsync(string source, IReadOnlyList<Track> tracks,
        CancellationToken cancellationToken = default)
    {
        // Keep prior fingerprints only for the exact same bytes and the same source.
        var previous = await LoadAsync(source, cancellationToken).ConfigureAwait(false);
        await SaveWithFingerprintsAsync(source, tracks,
            previous?.Fingerprints ?? new Dictionary<string, AudioFingerprint>(), cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task SaveWithFingerprintsAsync(string source, IReadOnlyList<Track> tracks,
        IReadOnlyDictionary<string, AudioFingerprint> bySha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(bySha256);
        cancellationToken.ThrowIfCancellationRequested();

        var (directory, path, normalized) = Locate(source);
        var trackArray = tracks.ToArray();
        var hashes = trackArray.Select(track => track.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fingerprints = new Dictionary<string, AudioFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in bySha256)
            if (hashes.Contains(pair.Key))
                fingerprints[pair.Key] = pair.Value;
        var snapshot = new TrackIndexSnapshot(1, normalized, DateTimeOffset.UtcNow, trackArray, fingerprints);

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var ownTemporary = false;
        try
        {
            var options =
                new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                ownTemporary = true;
                await JsonSerializer.SerializeAsync(stream, snapshot, Json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            ownTemporary = false;
        }
        finally
        {
            if (ownTemporary)
                File.Delete(temporary);
        }
    }

    public static async Task<TrackIndexSnapshot?> LoadAsync(string source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, path, normalized) = Locate(source);
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Access = FileAccess.Read, Mode = FileMode.Open, Options = FileOptions.Asynchronous,
                Share = FileShare.Read | FileShare.Delete
            });
            var snapshot = await JsonSerializer.DeserializeAsync<TrackIndexSnapshot>(stream, Json, cancellationToken)
                .ConfigureAwait(false);
            return snapshot is { SchemaVersion: 1, Tracks: not null, Fingerprints: not null } &&
                string.Equals(snapshot.Source, normalized,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? snapshot
                : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { return null; } // An invalid cache is never evidence that a source has no tracks.
    }

    private static (string Directory, string Path, string Normalized) Locate(string source)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var directory = CacheDirectory();
        var key = OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return (directory, Path.Combine(directory, $"{hash}.json"), normalized);
    }

    internal static string CacheDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("MUSIC_SORTER_INDEX_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
                throw new InvalidOperationException("Local application data is unavailable.");
            directory = Path.Combine(localData, "MusicSorter", "Index");
        }
        else if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("MUSIC_SORTER_INDEX_DIR must be an absolute path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }
}
