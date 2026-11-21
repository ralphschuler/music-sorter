using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Threading.Channels;
using MusicSorter.Core;

namespace MusicSorter.Metadata;

public sealed record ProfileIndexUpdate(
    IReadOnlyList<Track> Tracks,
    IReadOnlyDictionary<string, AudioFingerprint> Fingerprints,
    bool IsRefreshing,
    string? Message = null);

/// <summary>One cancellable, local-only worker for a currently open profile.</summary>
public sealed class ProfileIndexSession : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan FingerprintRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly StringComparer Paths = ProfileStore.PathComparer;
    private readonly SortProfile profile;
    private readonly ITagStore tags;
    private readonly IProgress<ProfileIndexUpdate> progress;
    private readonly CancellationTokenSource stop = new();
    private readonly object lifecycle = new();
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(Paths);
    private readonly ConcurrentDictionary<string, byte> failedWatchers = new(Paths);
    private readonly Dictionary<string, IReadOnlyList<Track>> rows = new(Paths);
    private readonly Dictionary<string, (IReadOnlyList<Track> Tracks, HashSet<string> Fingerprints)> published = new(Paths);
    private readonly ConcurrentDictionary<string, AudioFingerprint> fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> fingerprintTried = new(StringComparer.OrdinalIgnoreCase);
    private Task? worker;
    private Task? disposal;

    public ProfileIndexSession(SortProfile profile, ITagStore tags, IProgress<ProfileIndexUpdate> progress)
    {
        this.profile = ProfileStore.Normalize(profile);
        this.tags = tags ?? throw new ArgumentNullException(nameof(tags));
        this.progress = progress ?? throw new ArgumentNullException(nameof(progress));
    }

    public void Start()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            worker ??= Task.Run(() => RunAsync(stop.Token));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
            return new ValueTask(disposal ??= DisposeCoreAsync(worker));
    }

    private async Task DisposeCoreAsync(Task? pending)
    {
        await stop.CancelAsync().ConfigureAwait(false);
        wake.Writer.TryComplete();
        try
        {
            if (pending is not null)
                await pending.ConfigureAwait(false);
        }
        finally { stop.Dispose(); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var cacheWarning = false;
            foreach (var source in profile.Sources)
            {
                try
                {
                    var cached = await TrackIndex.LoadAsync(source, ct).ConfigureAwait(false);
                    if (cached is null)
                        continue;
                    var validTracks = cached.Tracks.Where(track =>
                        track is { Tags: not null, Sha256.Length: 64 } && track.Sha256.All(Uri.IsHexDigit) &&
                        IsBelow(track.Source, source)).ToArray();
                    rows[source] = validTracks;
                    var cacheHashes = validTracks.Select(track => track.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var validFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in cached.Fingerprints)
                    {
                        if (!cacheHashes.Contains(item.Key) || item.Value is not { Value.Length: > 0 } ||
                            !double.IsFinite(item.Value.DurationSeconds) || item.Value.DurationSeconds <= 0)
                            continue;
                        fingerprints.TryAdd(item.Key, item.Value);
                        validFingerprints.Add(item.Key);
                    }
                    if (validTracks.Length == cached.Tracks.Count && validFingerprints.Count == cached.Fingerprints.Count)
                        published[source] = (validTracks, validFingerprints);
                }
                catch (Exception ex) when (IsSourceIssue(ex)) { cacheWarning = true; }
            }
            Report(ct, isRefreshing: true,
                cacheWarning ? "Some local cache files could not be read. Checking the source folders." :
                rows.Count > 0 ? "Showing the saved index while checking source folders." : "Checking source folders.");

            while (!ct.IsCancellationRequested)
            {
                ConfigureWatchers();
                await RefreshAsync(ct).ConfigureAwait(false);
                await WaitForChangeOrPollAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            foreach (var watcher in watchers.Values)
                watcher.Dispose();
            watchers.Clear();
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var unavailable = 0;
        var cacheFailed = 0;
        var successful = new List<string>();
        foreach (var source in profile.Sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var scanned = await SortEngine.ScanAsync(source, tags, cancellationToken: ct).ConfigureAwait(false);
                rows[source] = scanned;
                successful.Add(source);
            }
            catch (Exception ex) when (IsSourceIssue(ex)) { unavailable++; }
        }

        var active = CombineRows();
        var currentHashes = active.Select(track => track.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var known in fingerprints.Keys)
            if (!currentHashes.Contains(known))
                fingerprints.TryRemove(known, out _);
        foreach (var known in fingerprintTried.Keys)
            if (!currentHashes.Contains(known))
                fingerprintTried.TryRemove(known, out _);

        var status = unavailable > 0
            ? $"{unavailable} source folder(s) are unavailable. Showing saved rows where available."
            : null;
        var executable = ChromaprintFingerprinter.FindInstalledExecutable();
        var candidates = executable is null ? [] : successful.SelectMany(source => rows[source])
            .GroupBy(track => track.Sha256, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .Where(track => !fingerprints.ContainsKey(track.Sha256) &&
                (!fingerprintTried.TryGetValue(track.Sha256, out var tried) ||
                 DateTimeOffset.UtcNow - tried >= FingerprintRetryDelay)).ToArray();
        var hadFingerprintWork = candidates.Length > 0;
        // The cache and fresh tags are usable before the lower-priority optional fingerprint work finishes.
        if (hadFingerprintWork)
            Report(ct, isRefreshing: false, status ?? "Tags and hashes are ready. Updating optional local fingerprints.", active);

        if (hadFingerprintWork)
        {
            var fingerprinter = new ChromaprintFingerprinter(executable);
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
                async (track, token) =>
                {
                    // Let a new filesystem event take priority over remaining optional work.
                    if (wake.Reader.TryPeek(out _))
                        return;
                    try
                    {
                        await RequireHashAsync(track, token).ConfigureAwait(false);
                        var value = await fingerprinter.ComputeAsync(track.Source, token).ConfigureAwait(false);
                        await RequireHashAsync(track, token).ConfigureAwait(false);
                        fingerprints[track.Sha256] = value;
                        fingerprintTried.TryRemove(track.Sha256, out _);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested &&
                        (IsSourceIssue(ex) || ex is InvalidDataException or OperationCanceledException or System.Text.Json.JsonException
                         or KeyNotFoundException or FormatException or OverflowException))
                    {
                        fingerprintTried[track.Sha256] = DateTimeOffset.UtcNow;
                    }
                }).ConfigureAwait(false);
        }

        foreach (var source in successful)
        {
            var fresh = rows[source];
            var covered = fresh.Select(track => track.Sha256).Where(fingerprints.ContainsKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (published.TryGetValue(source, out var previous) && previous.Tracks.SequenceEqual(fresh) &&
                previous.Fingerprints.SetEquals(covered))
                continue;
            try
            {
                await TrackIndex.SaveWithFingerprintsAsync(source, fresh, fingerprints, ct).ConfigureAwait(false);
                published[source] = (fresh, covered);
            }
            catch (Exception ex) when (IsSourceIssue(ex)) { cacheFailed++; }
        }
        if (cacheFailed > 0)
            status = string.Join(" ", new[] { status, "The refreshed index could not be saved for some source folders." }
                .Where(part => part is not null));
        Report(ct, isRefreshing: false, status, active);
    }

    private void ConfigureWatchers()
    {
        foreach (var source in profile.Sources)
        {
            var available = Directory.Exists(source);
            var replace = failedWatchers.TryRemove(source, out _);
            if ((!available || replace) && watchers.Remove(source, out var previous))
                previous.Dispose();
            if (!available || watchers.ContainsKey(source) || IsLinked(source))
                continue;
            FileSystemWatcher? watching = null;
            try
            {
                watching = new FileSystemWatcher(source)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watching.Changed += OnChange;
                watching.Created += OnChange;
                watching.Deleted += OnChange;
                watching.Renamed += OnRename;
                watching.Error += (_, _) =>
                {
                    failedWatchers.TryAdd(source, 0);
                    wake.Writer.TryWrite(true);
                };
                watching.EnableRaisingEvents = true;
                watchers.Add(source, watching);
                watching = null;
            }
            catch (Exception ex) when (IsSourceIssue(ex) || ex is System.ComponentModel.Win32Exception)
            {
                // Polling is still active, including on filesystems with no usable watch backend.
            }
            finally { watching?.Dispose(); }
        }
    }

    private void OnChange(object sender, FileSystemEventArgs args)
    {
        if (!IsOwnStorage(args.FullPath))
            wake.Writer.TryWrite(true);
    }

    private void OnRename(object sender, RenamedEventArgs args)
    {
        if (!IsOwnStorage(args.FullPath) || !IsOwnStorage(args.OldFullPath))
            wake.Writer.TryWrite(true);
    }

    private static bool IsOwnStorage(string path)
    {
        try { return IsBelow(path, TrackIndex.CacheDirectory()) || IsBelow(path, ProfileStore.Locate()); }
        catch (Exception ex) when (IsSourceIssue(ex)) { return false; }
    }

    private async Task WaitForChangeOrPollAsync(CancellationToken ct)
    {
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
        poll.CancelAfter(PollInterval);
        try
        {
            if (!await wake.Reader.WaitToReadAsync(poll.Token).ConfigureAwait(false))
                return;
            // Coalesce bursts without allowing a folder copied continuously to postpone refresh forever.
            await Task.Delay(Debounce, ct).ConfigureAwait(false);
            while (wake.Reader.TryRead(out _)) { }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        Report(ct, isRefreshing: true, "Checking source folders.");
    }

    private void Report(CancellationToken ct, bool isRefreshing, string? message, IReadOnlyList<Track>? tracks = null)
    {
        ct.ThrowIfCancellationRequested();
        tracks ??= CombineRows();
        var hashes = tracks.Select(track => track.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byHash = fingerprints.Where(pair => hashes.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        progress.Report(new ProfileIndexUpdate(
            tracks, new ReadOnlyDictionary<string, AudioFingerprint>(byHash), isRefreshing, message));
    }

    private IReadOnlyList<Track> CombineRows() => Array.AsReadOnly(profile.Sources.Where(rows.ContainsKey)
        .SelectMany(source => rows[source]).DistinctBy(track => track.Source, Paths)
        .OrderBy(track => track.Source, Paths).ThenBy(track => track.Source, StringComparer.Ordinal).ToArray());

    private static async Task RequireHashAsync(Track track, CancellationToken ct)
    {
        if (IsLinked(track.Source))
            throw new IOException("A scanned file or one of its parent folders is no longer a plain local path.");
        await using var stream = new FileStream(track.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!actual.Equals(track.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The scanned bytes changed before fingerprinting finished.");
    }

    private static bool IsLinked(string path)
    {
        try
        {
            for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    return true;
            return false;
        }
        catch (Exception ex) when (IsSourceIssue(ex)) { return true; }
    }

    private static bool IsBelow(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        var relative = Path.GetRelativePath(folder, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsSourceIssue(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException;
}
