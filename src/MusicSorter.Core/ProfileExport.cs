using System.Security.Cryptography;

namespace MusicSorter.Core;

public static partial class SortEngine
{
    private sealed class DestinationCollisionException(string message) : IOException(message);

    /// <summary>Validates the entire set without creating, deleting, or changing files.</summary>
    public static void ValidateProfileFolders(IEnumerable<string> sources, string output)
    {
        var roots = ValidateSourceRoots(sources);
        ValidateOutputRoot(roots, CheckedRoot(output));
    }

    /// <summary>
    /// Copies one global plan produced by <see cref="Plan"/>. Exact pre-existing copies are
    /// skipped; conflicting files are never modified. A per-file failure does not stop the batch.
    /// </summary>
    public static async Task<IReadOnlyList<ProfileExportResult>> ExportProfileAsync(
        IEnumerable<string> sources, IEnumerable<PlanItem> plan, ITagStore tags,
        IProgress<ProfileExportResult>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tags);
        cancellationToken.ThrowIfCancellationRequested();
        var roots = ValidateSourceRoots(sources);
        var items = plan.ToArray();

        // The expansion chosen in Plan retains its output root. Never infer it from individual
        // destinations: doing so would miss an output root that is an ancestor of a source.
        var outputRoots = items.Where(item => item.OutputRoot is not null)
            .Select(item => CheckedRoot(item.OutputRoot!))
            .Distinct(WindowsPaths).ToArray();
        if (outputRoots.Length > 1 || items.Any(item =>
                item.Status != PlanStatus.Duplicate && item.OutputRoot is null))
            throw new ArgumentException("Pass one global plan created by SortEngine.Plan.", nameof(plan));
        var outputRoot = outputRoots.SingleOrDefault();
        if (outputRoot is not null)
            ValidateOutputRoot(roots, outputRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var destinations = new HashSet<string>(WindowsPaths);
        var results = new List<ProfileExportResult>(items.Length);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExportProfileItemAsync(item, roots, outputRoot, destinations, tags, cancellationToken)
                .ConfigureAwait(false);
            results.Add(result);
            progress?.Report(result);
        }
        return results;
    }

    private static string[] ValidateSourceRoots(IEnumerable<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var roots = sources.Select(CheckedRoot).ToArray();
        if (roots.Length == 0)
            throw new ArgumentException("Select at least one source folder.", nameof(sources));
        for (var i = 0; i < roots.Length; i++)
        {
            EnsureUnlinkedDirectories(roots[i], create: false);
            for (var j = 0; j < i; j++)
                if (ProfilePathsOverlap(roots[i], roots[j]))
                    throw new IOException("Source folders must be distinct and not overlap.");
        }
        return roots;
    }

    private static void ValidateOutputRoot(string[] roots, string output)
    {
        if (roots.Any(root => ProfilePathsOverlap(root, output)))
            throw new IOException("Source and output folders must not overlap.");
        EnsureUnlinkedDirectories(output, create: false, allowMissing: true);
    }

    private static bool ProfilePathsOverlap(string left, string right)
    {
        left = CanonicalUnicode(left);
        right = CanonicalUnicode(right);
        return SameOrWithin(left, right) || SameOrWithin(right, left);
    }

    private static async Task<ProfileExportResult> ExportProfileItemAsync(
        PlanItem item, string[] roots, string? outputRoot, HashSet<string> destinations, ITagStore tags,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!IsSha256(item.Track.Sha256))
                throw new ArgumentException("The plan has an invalid SHA-256.");
            var original = CheckedRoot(item.Track.Source);
            var sourceComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!roots.Any(root => Within(original, root, sourceComparison)) ||
                !AudioExtensions.Contains(Path.GetExtension(original)))
                throw new IOException("A planned source is outside the selected sources or has an unsupported extension.");
            EnsureUnlinkedDirectories(Path.GetDirectoryName(original)!, create: false);
            EnsurePlainFile(original);
            if (item.Status == PlanStatus.Duplicate)
            {
                // A changed former duplicate is not truly a duplicate anymore; make it visible
                // as a failure rather than silently omitting it from the export.
                await VerifySourceHashAsync(original, item.Track.Sha256, cancellationToken).ConfigureAwait(false);
                return new(item, "SkippedDuplicate");
            }
            if (item.Status is not (PlanStatus.Ready or PlanStatus.Variant) ||
                item.Destination is null || outputRoot is null)
                throw new ArgumentException("The plan has an unsupported status or missing destination.");
            var destination = CheckedRoot(item.Destination);
            if (!Within(destination, outputRoot, StringComparison.Ordinal) ||
                !Path.GetExtension(destination).Equals(Path.GetExtension(original), StringComparison.OrdinalIgnoreCase) ||
                Path.GetRelativePath(outputRoot, destination).Split(Path.DirectorySeparatorChar)
                    .Any(segment => !string.Equals(segment, Sanitize(segment), StringComparison.Ordinal)))
                throw new IOException("A planned destination is outside the selected output or unsafe.");
            if (!destinations.Add(destination))
                throw new DestinationCollisionException("The plan repeats this destination.");

            var directory = Path.GetDirectoryName(destination)!;
            EnsureUnlinkedDirectories(directory, create: false, allowMissing: true);
            if (FindExistingDestination(destination) is { } existing)
            {
                if ((existing.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new DestinationCollisionException("Destination exists and is not an unlinked file.");
                // Verify that the indexed source has not changed, even when we do not copy it.
                await VerifySourceHashAsync(original, item.Track.Sha256, cancellationToken).ConfigureAwait(false);
                EnsureUnlinkedDirectories(directory, create: false);
                EnsurePlainFile(existing.Path);
                if (await MatchesSha256Async(existing.Path, item.Track.Sha256, cancellationToken).ConfigureAwait(false))
                    return new(item, "AlreadyPresent", "An existing output has the same SHA-256.");
                throw new DestinationCollisionException("Destination exists with different bytes; it was left untouched.");
            }

            await ExportOneAsync(item, original, destination, tags, cancellationToken).ConfigureAwait(false);
            return new(item, "Copied");
        }
        catch (DestinationCollisionException ex)
        {
            return new(item, "Conflict", ex.Message);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or AccessViolationException))
        {
            return new(item, "Failed", ex.Message);
        }
    }

    private static async Task VerifySourceHashAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        if (!await MatchesSha256Async(path, sha256, cancellationToken).ConfigureAwait(false))
            throw new IOException("The source changed after scanning; rescan before exporting.");
    }

    private static async Task<bool> MatchesSha256Async(string path, string sha256, CancellationToken cancellationToken)
    {
        await using var stream = OpenSource(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return actual.Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Path, FileAttributes Attributes)? FindExistingDestination(string destination)
    {
        // Search siblings on any platform so a different spelling on case-sensitive storage
        // does not create two outputs that cannot coexist on Windows.
        var directory = Path.GetDirectoryName(destination);
        var name = CanonicalUnicode(Path.GetFileName(destination)).TrimEnd('.', ' ');
        if (directory is not null && Directory.Exists(directory))
        {
            var matches = Directory.EnumerateFileSystemEntries(directory)
                .Where(path => WindowsPaths.Equals(CanonicalUnicode(Path.GetFileName(path)).TrimEnd('.', ' '), name))
                .Take(2).ToArray();
            if (matches.Length > 1)
                throw new DestinationCollisionException("Multiple Windows-equivalent destinations already exist.");
            if (matches.Length == 1)
                return (matches[0], File.GetAttributes(matches[0]));
        }
        var attributes = GetAttributes(destination);
        return attributes.HasValue ? (destination, attributes.Value) : null;
    }
}
