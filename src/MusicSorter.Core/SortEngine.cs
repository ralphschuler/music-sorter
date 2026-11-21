using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicSorter.Core;

public static partial class SortEngine
{
    private const int BufferSize = 128 * 1024;
    private static readonly StringComparer WindowsPaths = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".wav", ".aiff", ".aif",
        ".ogg", ".oga", ".opus", ".wma", ".ape", ".wv", ".mpc", ".alac"
    };

    public static async Task<IReadOnlyList<Track>> ScanAsync(
        string source, ITagStore tags, IProgress<Track>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var root = CheckedRoot(source);
        EnsureUnlinkedDirectories(root, create: false);
        cancellationToken.ThrowIfCancellationRequested();
        var found = new ConcurrentBag<Track>();
        await Parallel.ForEachAsync(EnumerateAudio(root, cancellationToken), new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken
        }, async (path, token) =>
        {
            string hash;
            try
            {
                EnsurePlainFile(path);
                await using var stream = OpenSource(path);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Files removed, locked, linked, or otherwise unreadable are not indexed.
                return;
            }

            TrackTags metadata;
            try
            {
                EnsurePlainFile(path);
                metadata = tags.Read(path) ?? new TrackTags();
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or AccessViolationException))
            {
                // A decoder failure is not a positive tag result; keep the content hash.
                metadata = new TrackTags();
            }
            token.ThrowIfCancellationRequested();
            var track = new Track(path, hash, metadata);
            found.Add(track);
            progress?.Report(track);
        }).ConfigureAwait(false);

        return found.OrderBy(t => t.Source, WindowsPaths).ThenBy(t => t.Source, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<PlanItem> Plan(IEnumerable<Track> tracks, string output, string template)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var root = CheckedRoot(output);
        var segments = ParseTemplate(template);
        var ordered = tracks.OrderBy(t => t.Source, WindowsPaths)
            .ThenBy(t => t.Source, StringComparer.Ordinal).ThenBy(t => t.Sha256, WindowsPaths).ToArray();
        var variantKeys = ordered.Select(t =>
                (Key: ArtistTitle(t.Tags), Hash: t.Sha256))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!)
            .Where(group => group.Select(x => x.Hash).Distinct(WindowsPaths).Take(2).Count() > 1)
            .Select(group => group.Key).ToHashSet();
        var seenHashes = new HashSet<string>(WindowsPaths);
        var projectPaths = new HashSet<string>(WindowsPaths);
        var directoryPaths = new HashSet<string>(WindowsPaths);
        var directoryChoices = new Dictionary<string, string>(WindowsPaths);
        var result = new List<PlanItem>(ordered.Length);
        foreach (var track in ordered)
        {
            if (!IsSha256(track.Sha256))
                throw new ArgumentException("A track has an invalid SHA-256 digest.", nameof(tracks));
            if (!seenHashes.Add(track.Sha256))
            {
                result.Add(new(track, null, PlanStatus.Duplicate) { OutputRoot = root });
                continue;
            }

            var key = ArtistTitle(track.Tags);
            var variant = key is not null && variantKeys.Contains(key);
            var names = segments.Select(segment => RenderSegment(segment, track)).ToArray();
            var extension = Path.GetExtension(track.Source);
            if (!AudioExtensions.Contains(extension))
                throw new ArgumentException("A track has an unsupported audio extension.", nameof(tracks));

            var parent = root;
            foreach (var name in names.AsSpan(0, names.Length - 1))
            {
                var original = Path.Combine(parent, name);
                if (!directoryChoices.TryGetValue(original, out var chosen))
                {
                    chosen = original;
                    for (var i = 2; projectPaths.Contains(chosen) || directoryPaths.Contains(chosen); i++)
                        chosen = Path.Combine(parent, WithSuffix(name, "~" + i.ToString(CultureInfo.InvariantCulture), ""));
                    directoryChoices.Add(original, chosen);
                    directoryPaths.Add(chosen);
                }
                parent = chosen;
            }

            var file = names[^1];
            var stem = file.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? file[..^extension.Length] : file;
            stem = Sanitize(stem);
            var stableSuffix = "~" + track.Sha256[..12].ToLowerInvariant();
            var target = Path.Combine(parent, WithSuffix(stem, variant ? stableSuffix : "", extension));
            if (projectPaths.Contains(target) || directoryPaths.Contains(target))
            {
                target = Path.Combine(parent, WithSuffix(stem, stableSuffix, extension));
                for (var i = 2; projectPaths.Contains(target) || directoryPaths.Contains(target); i++)
                    target = Path.Combine(parent, WithSuffix(stem, stableSuffix + "-" + i.ToString(CultureInfo.InvariantCulture), extension));
            }
            projectPaths.Add(target);
            result.Add(new(track, target, variant ? PlanStatus.Variant : PlanStatus.Ready) { OutputRoot = root });
        }
        return result;
    }

    public static async Task ExportAsync(
        string source, IEnumerable<PlanItem> plan, ITagStore tags, IProgress<PlanItem>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tags);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceRoot = CheckedRoot(source);
        EnsureUnlinkedDirectories(sourceRoot, create: false);

        // Validate the whole plan before creating any directories or files.
        var checkedItems = new List<(PlanItem Item, string Original, string Destination)>();
        var targets = new HashSet<string>(WindowsPaths);
        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Status == PlanStatus.Duplicate)
                continue;
            if (item.Destination is null || !IsSha256(item.Track.Sha256))
                throw new ArgumentException("The plan has a missing destination or invalid SHA-256.", nameof(plan));
            var original = CheckedRoot(item.Track.Source);
            if (!Within(original, sourceRoot, StringComparison.Ordinal) || !AudioExtensions.Contains(Path.GetExtension(original)))
                throw new IOException("A planned source is outside the selected source or has an unsupported extension.");
            var destination = CheckedRoot(item.Destination);
            var parent = Path.GetDirectoryName(destination) ?? throw new IOException("A destination has no parent directory.");
            var outputRoot = item.OutputRoot is null ? parent : CheckedRoot(item.OutputRoot);
            if (SameOrWithin(outputRoot, sourceRoot) || SameOrWithin(sourceRoot, outputRoot) ||
                !Within(destination, outputRoot, StringComparison.Ordinal) || !targets.Add(destination))
                throw new IOException("Source and output overlap, a destination leaves the output, or the plan repeats a destination.");
            if (Path.GetExtension(destination).Equals(Path.GetExtension(original), StringComparison.OrdinalIgnoreCase) is false)
                throw new IOException("A destination changes the audio format extension.");
            foreach (var segment in Path.GetRelativePath(outputRoot, destination).Split(Path.DirectorySeparatorChar))
                if (!string.Equals(segment, Sanitize(segment), StringComparison.Ordinal))
                    throw new IOException("A destination is not a valid Windows path.");

            EnsureUnlinkedDirectories(Path.GetDirectoryName(original)!, create: false);
            EnsurePlainFile(original);
            EnsureUnlinkedDirectories(parent, create: false, allowMissing: true);
            EnsureNoDestination(destination);
            checkedItems.Add((item, original, destination));
        }
        foreach (var target in targets)
            for (var dir = Path.GetDirectoryName(target); dir is not null; dir = Path.GetDirectoryName(dir))
                if (targets.Contains(dir))
                    throw new IOException("A planned export file is also a destination directory.");

        foreach (var (item, original, destination) in checkedItems)
        {
            await ExportOneAsync(item, original, destination, tags, cancellationToken).ConfigureAwait(false);
            progress?.Report(item);
        }
    }

    private static async Task ExportOneAsync(
        PlanItem item, string original, string destination, ITagStore tags, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureUnlinkedDirectories(Path.GetDirectoryName(original)!, create: false);
        EnsurePlainFile(original);
        var directory = Path.GetDirectoryName(destination)!;
        EnsureUnlinkedDirectories(directory, create: true);
        EnsureNoDestination(destination);
        var stage = Path.Combine(directory, ".music-sorter-" + Guid.NewGuid().ToString("N") + Path.GetExtension(destination));
        var ownsStage = false;
        try
        {
            await using (var input = OpenSource(original))
            await using (var outputFile = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsStage = true;
                await input.CopyToAsync(outputFile, BufferSize, cancellationToken).ConfigureAwait(false);
                await outputFile.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var copy = OpenSource(stage))
            {
                var copiedHash = Convert.ToHexString(await SHA256.HashDataAsync(copy, cancellationToken).ConfigureAwait(false));
                if (!copiedHash.Equals(item.Track.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The source changed after scanning; rescan before exporting.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureUnlinkedDirectories(directory, create: false);
            EnsurePlainFile(stage);
            if (HasMetadata(item.Track.Tags))
            {
                var existing = tags.Read(stage) ?? throw new IOException("The staged metadata could not be read.");
                tags.FillMissing(stage, item.Track.Tags);
                EnsurePlainFile(stage);
                var actual = tags.Read(stage) ?? throw new IOException("The staged metadata could not be read back.");
                VerifyMetadata(existing, item.Track.Tags, actual);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureUnlinkedDirectories(directory, create: false);
            EnsureNoDestination(destination);
            try { File.Move(stage, destination, overwrite: false); }
            catch (IOException)
            {
                EnsureNoDestination(destination);
                throw;
            }
            ownsStage = false;
        }
        finally
        {
            if (ownsStage)
            {
                // Do not follow a changed staging-directory chain during error cleanup.
                try
                {
                    EnsureUnlinkedDirectories(directory, create: false);
                    File.Delete(stage);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static FileStream OpenSource(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static IEnumerable<string> EnumerateAudio(string root, CancellationToken token)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            using var entries = TryEnumerate(directory);
            if (entries is null) continue;
            while (TryNext(entries))
            {
                var path = entries.Current;
                token.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                    directories.Push(path);
                else if (AudioExtensions.Contains(Path.GetExtension(path)))
                    yield return path;
            }
        }
    }

    private static IEnumerator<string>? TryEnumerate(string directory)
    {
        try
        {
            EnsureUnlinkedDirectories(directory, create: false);
            return Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool TryNext(IEnumerator<string> entries)
    {
        try { return entries.MoveNext(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool HasMetadata(TrackTags tags) =>
        !string.IsNullOrWhiteSpace(tags.Artist) || !string.IsNullOrWhiteSpace(tags.Title) ||
        !string.IsNullOrWhiteSpace(tags.Genre) || tags.Year is > 0 and <= 9999;

    private static void VerifyMetadata(TrackTags before, TrackTags proposed, TrackTags actual)
    {
        static string Choose(string prior, string fill) =>
            !string.IsNullOrWhiteSpace(prior) ? prior.Trim() : fill.Trim();
        var expectedYear = before.Year != 0 ? before.Year : proposed.Year is > 0 and <= 9999 ? proposed.Year : 0U;
        if (actual.Artist.Trim() != Choose(before.Artist, proposed.Artist) ||
            actual.Title.Trim() != Choose(before.Title, proposed.Title) ||
            actual.Genre.Trim() != Choose(before.Genre, proposed.Genre) || actual.Year != expectedYear)
            throw new IOException("The metadata update could not be verified on the staged copy.");
    }

    private static string[] ParseTemplate(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        var parts = template.Replace('\\', '/').Split('/');
        if (parts.Length > 32 || parts.Any(s => string.IsNullOrWhiteSpace(s) || s.Trim() is "." or ".." || s.Contains(':')))
            throw new ArgumentException("Use a relative template with up to 32 nonempty path parts.", nameof(template));
        foreach (var part in parts)
        {
            foreach (Match token in TemplateToken().Matches(part))
                if (!KnownTokens.Contains(token.Groups[1].Value))
                    throw new ArgumentException($"Unknown template token: {token.Value}", nameof(template));
            var literal = TemplateToken().Replace(part, "");
            if (literal.Contains('{') || literal.Contains('}'))
                throw new ArgumentException("Template tokens must use matching braces.", nameof(template));
        }
        return parts;
    }

    private static readonly HashSet<string> KnownTokens = new(WindowsPaths)
    {
        "Artist", "Title", "Genre", "Year", "ArtistInitial", "FileName", "Extension"
    };

    private static string RenderSegment(string template, Track track)
    {
        return Sanitize(TemplateToken().Replace(template, match =>
        {
            var value = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "artist" => track.Tags.Artist,
                "title" => track.Tags.Title,
                "genre" => track.Tags.Genre,
                "year" => track.Tags.Year == 0 ? "" : track.Tags.Year.ToString(CultureInfo.InvariantCulture),
                "artistinitial" => string.IsNullOrWhiteSpace(track.Tags.Artist) ? "" :
                    StringInfo.GetNextTextElement(track.Tags.Artist.Trim()).ToUpperInvariant(),
                "filename" => Path.GetFileNameWithoutExtension(track.Source),
                "extension" => Path.GetExtension(track.Source).TrimStart('.'),
                _ => ""
            };
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }));
    }

    [GeneratedRegex(@"\{([A-Za-z]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateToken();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedWindowsName();

    private static string Sanitize(string value)
    {
        // Normalize to one representation for cross-platform case/collision handling.
        var normalized = CanonicalUnicode(value);
        var safe = new string(normalized.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
        safe = Cut(safe.Trim().TrimEnd('.', ' '), 200).TrimEnd('.', ' ');
        if (safe.Length == 0)
            return "Unknown";
        if (ReservedWindowsName().IsMatch(safe.Split('.', 2)[0].TrimEnd(' ')))
            return "_" + Cut(safe, 199);
        return safe;
    }

    private static string Cut(string value, int length, int byteLimit = 230)
    {
        var bytes = 0;
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf16SequenceLength > length || bytes + rune.Utf8SequenceLength > byteLimit)
                break;
            used += rune.Utf16SequenceLength;
            bytes += rune.Utf8SequenceLength;
        }
        return value[..used];
    }

    private static string WithSuffix(string stem, string suffix, string extension) =>
        Cut(stem, 200 - suffix.Length - extension.Length, 230 - Encoding.UTF8.GetByteCount(suffix + extension))
            .TrimEnd('.', ' ') + suffix + extension;

    private static string CanonicalUnicode(string value)
    {
        // Malformed UTF-16 from an external tag library is replaced, not allowed to break planning.
        try { return value.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(value)).Normalize(NormalizationForm.FormKC); }
    }

    private static (string Artist, string Title)? ArtistTitle(TrackTags tags)
    {
        if (string.IsNullOrWhiteSpace(tags.Artist) || string.IsNullOrWhiteSpace(tags.Title)) return null;
        return (Spaces().Replace(CanonicalUnicode(tags.Artist).Trim(), " ").ToUpperInvariant(),
            Spaces().Replace(CanonicalUnicode(tags.Title).Trim(), " ").ToUpperInvariant());
    }

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string CheckedRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsMacOS()) return full;

        // macOS's /var and /tmp system aliases are not user-created links.
        // Convert only if their system target agrees; every other link is rejected.
        foreach (var alias in new[] { "/var", "/tmp" })
            if (SameOrWithin(full, alias, StringComparison.Ordinal) &&
                string.Equals(Directory.ResolveLinkTarget(alias, returnFinalTarget: false)?.FullName,
                    "/private" + alias, StringComparison.Ordinal))
                return "/private" + full;
        return full;
    }

    private static bool SameOrWithin(string path, string root, StringComparison comparison = StringComparison.OrdinalIgnoreCase) =>
        string.Equals(path, root, comparison) || Within(path, root, comparison);

    private static bool Within(string path, string root, StringComparison comparison)
    {
        if (string.Equals(path, root, comparison)) return false;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    private static void EnsureUnlinkedDirectories(string path, bool create, bool allowMissing = false)
    {
        var chain = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            chain.Push(current);
        while (chain.TryPop(out var directory))
        {
            var attrs = GetAttributes(directory);
            if (attrs is null)
            {
                if (!create && allowMissing) return;
                if (!create) throw new DirectoryNotFoundException(directory);
                EnsureNoDestination(directory);
                Directory.CreateDirectory(directory);
                attrs = GetAttributes(directory);
            }
            if (attrs is null || (attrs.Value & FileAttributes.ReparsePoint) != 0 ||
                (attrs.Value & FileAttributes.Directory) == 0)
                throw new IOException("A path is linked or is not a directory: " + directory);
        }
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static void EnsurePlainFile(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new IOException("A source or staged file is a link or not a regular file.");
    }

    private static void EnsureNoDestination(string path)
    {
        if (GetAttributes(path) is not null)
            throw new DestinationCollisionException("Destination already exists: " + path);
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent)) return;
        var name = CanonicalUnicode(Path.GetFileName(path)).TrimEnd('.', ' ');
        if (Directory.EnumerateFileSystemEntries(parent).Any(s =>
                WindowsPaths.Equals(CanonicalUnicode(Path.GetFileName(s)).TrimEnd('.', ' '), name)))
            throw new DestinationCollisionException("A Windows-equivalent destination already exists: " + path);
    }
}
