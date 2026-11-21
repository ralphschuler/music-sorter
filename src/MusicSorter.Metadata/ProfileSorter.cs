using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicSorter.Core;

namespace MusicSorter.Metadata;

/// <summary>Retains verified copies across changes to the global sort plan.</summary>
public static class ProfileSorter
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false
    };

    private sealed record Receipt(string RelativePath, string FinalSha256);
    private sealed record Ledger(int SchemaVersion, Guid ProfileId, string OutputRoot,
        Dictionary<string, Receipt> Entries);

    public static async Task<IReadOnlyList<ProfileExportResult>> ExportAsync(
        SortProfile profile, IEnumerable<PlanItem> plan, ITagStore tags,
        IProgress<ProfileExportResult>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(tags);
        ct.ThrowIfCancellationRequested();
        var root = Normalize(profile.Output);
        var sources = profile.Sources.Select(Normalize).ToArray();
        SortEngine.ValidateProfileFolders(sources, root);
        var items = plan.ToArray();
        var results = new List<ProfileExportResult>(items.Length);
        if (items.Length == 0) return results;

        void Report(ProfileExportResult result)
        {
            results.Add(result);
            progress?.Report(result);
        }

        var stateDirectory = Environment.GetEnvironmentVariable("MUSIC_SORTER_PROFILE_DIR");
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
                throw new InvalidOperationException("Local application data is unavailable.");
            stateDirectory = Path.Combine(localData, "MusicSorter", "Profiles");
        }

        stateDirectory = Path.Combine(Normalize(stateDirectory), "Receipts");
        if (sources.Append(root).Any(path => Overlaps(stateDirectory, path)))
            throw new IOException("The receipt directory must not overlap source or output directories.");

        var rootKey = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var rootDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootKey)));
        var receiptPath = Path.Combine(stateDirectory, $"{profile.Id:N}.{rootDigest}.json");
        var lockPath = receiptPath + ".lock";

        // The durable lock path is not unlinked after release: two lockers must never open
        // different inodes for the same ledger. Do not wait with an unresponsive UI.
        EnsureDirectory(stateDirectory, create: true);
        EnsureRegularOrMissing(lockPath);
        await using var sync = new FileStream(lockPath, Options(FileMode.OpenOrCreate, FileAccess.ReadWrite));
        EnsureDirectory(stateDirectory);
        EnsureRegularOrMissing(lockPath);

        Ledger ledger;
        byte[]? expectedLedger;
        var managedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            expectedLedger = await ReadLedgerAsync(receiptPath, ct).ConfigureAwait(false);
            ledger = expectedLedger is null
                ? new Ledger(1, profile.Id, root, new Dictionary<string, Receipt>(StringComparer.OrdinalIgnoreCase))
                : JsonSerializer.Deserialize<Ledger>(expectedLedger, Json)
                    ?? throw new InvalidDataException("The export receipt is empty.");

            if (ledger.SchemaVersion != 1 || ledger.ProfileId != profile.Id ||
                !string.Equals(ledger.OutputRoot, root, PathComparison) || ledger.Entries is null)
                throw new InvalidDataException("The export receipt does not match this profile and output folder.");

            var entries = new Dictionary<string, Receipt>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in ledger.Entries)
            {
                if (!IsSha256(entry.Key) || entry.Value is null || !IsSha256(entry.Value.FinalSha256) ||
                    !managedPaths.Add(PathKey(ResolveReceiptPath(root, entry.Value.RelativePath))) || !entries.TryAdd(entry.Key, entry.Value))
                    throw new InvalidDataException("The export receipt contains invalid or ambiguous entries.");
            }
            ledger = ledger with { Entries = entries };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                Report(item.Status == PlanStatus.Duplicate
                    ? new(item, "SkippedDuplicate")
                    : new(item, "Conflict", $"Export receipt could not be trusted; no files were copied. {ex.Message}"));
            }
            return results;
        }

        var plannedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Status == PlanStatus.Duplicate)
            {
                Report(new(item, "SkippedDuplicate"));
                continue;
            }

            string destination, original;
            try
            {
                if (!IsSha256(item.Track.Sha256) || item.Status is not (PlanStatus.Ready or PlanStatus.Variant))
                    throw new InvalidDataException("The planned track status or source SHA-256 is invalid.");
                destination = Normalize(item.Destination ?? throw new InvalidDataException("No planned destination."));
                original = Normalize(item.Track.Source);
                if (!StrictlyWithin(destination, root) || !sources.Any(s => StrictlyWithin(original, s)))
                    throw new InvalidDataException("A plan path is outside the selected source or output.");
                if (!plannedDestinations.Add(PathKey(destination)))
                {
                    Report(new(item, "Conflict", "The plan repeats this destination."));
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or InvalidDataException)
            {
                Report(new(item, "Failed", ex.Message));
                continue;
            }

            if (ledger.Entries.TryGetValue(item.Track.Sha256, out var previous))
            {
                // Keep an invalid managed entry as an explicit conflict. Dropping it would make
                // the next run forget that this original was previously exported elsewhere.
                string prior;
                try
                {
                    prior = ResolveReceiptPath(root, previous.RelativePath);
                    if (!string.Equals(await HashFileAsync(prior, ct).ConfigureAwait(false),
                            previous.FinalSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Previously exported bytes no longer match the receipt.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
                {
                    Report(new(item, "Conflict", $"Previous export could not be verified; nothing was copied. {ex.Message}"));
                    continue;
                }
                try
                {
                    if (!string.Equals(await HashFileAsync(original, ct).ConfigureAwait(false),
                            item.Track.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The source changed after scanning; rescan before exporting.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report(new(item, "Failed", ex.Message));
                    continue;
                }

                Report(new(item with { Destination = prior }, "AlreadyPresent",
                    "Verified an earlier export for this profile."));
                continue;
            }

            if (managedPaths.Contains(PathKey(destination)))
            {
                Report(new(item, "Conflict", "This destination is reserved by the receipt for another original."));
                continue;
            }

            var result = (await SortEngine.ExportProfileAsync(sources, new[] { item }, tags,
                cancellationToken: ct).ConfigureAwait(false)).Single();
            // An identical pre-existing file may belong to the user. Never claim it as managed.
            if (result.Outcome != "Copied")
            {
                Report(result);
                continue;
            }

            // Once Core has committed a file, try to retain its receipt even if the caller
            // cancels. Do not remove a successfully published file on a receipt write failure.
            var published = Normalize(result.Item.Destination ?? destination);
            try
            {
                if (!StrictlyWithin(published, root))
                    throw new IOException("The published path is outside the selected output.");
                var finalHash = await HashFileAsync(published, CancellationToken.None).ConfigureAwait(false);
                ledger.Entries.Add(item.Track.Sha256,
                    new Receipt(Path.GetRelativePath(root, published), finalHash));
                expectedLedger = await SaveLedgerAsync(receiptPath, ledger, expectedLedger).ConfigureAwait(false);
                managedPaths.Add(PathKey(published));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                Report(result with { Message = $"Copied, but its receipt could not be saved. Resolve the state issue before exporting again: {ex.Message}" });
                throw new IOException("Export stopped after the receipt write failed. The published file was not modified.", ex);
            }

            Report(result);
        }

        return results;
    }

    private static async Task<byte[]?> ReadLedgerAsync(string path, CancellationToken ct)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        if (!EnsureRegularOrMissing(path)) return null;
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> SaveLedgerAsync(string path, Ledger ledger, byte[]? expected)
    {
        var parent = Path.GetDirectoryName(path)!;
        EnsureDirectory(parent);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ledger, Json);
        var temporary = Path.Combine(parent, $".receipt-{Guid.NewGuid():N}.tmp");
        var ownsTemporary = false;
        try
        {
            await using (var file = new FileStream(temporary, Options(FileMode.CreateNew, FileAccess.Write)))
            {
                ownsTemporary = true;
                await file.WriteAsync(bytes).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            var current = await ReadLedgerAsync(path, CancellationToken.None).ConfigureAwait(false);
            if (current is null ? expected is not null : expected is null || !current.AsSpan().SequenceEqual(expected))
                throw new IOException("The previous receipt changed during export.");
            EnsureDirectory(parent);
            File.Move(temporary, path, overwrite: expected is not null);
            ownsTemporary = false;
            return bytes;
        }
        finally
        {
            if (ownsTemporary)
            {
                try
                {
                    EnsureDirectory(parent);
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static FileStreamOptions Options(FileMode mode, FileAccess access)
    {
        var options = new FileStreamOptions
        {
            Mode = mode, Access = access, Share = FileShare.None, Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows() && mode is FileMode.CreateNew or FileMode.OpenOrCreate)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        if (!EnsureRegularOrMissing(path)) throw new FileNotFoundException("The file is missing.", path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        EnsureDirectory(Path.GetDirectoryName(path)!);
        if (!EnsureRegularOrMissing(path)) throw new FileNotFoundException("The file disappeared.", path);
        return Convert.ToHexString(digest);
    }

    private static string ResolveReceiptPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains(':') || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' ')))
            throw new InvalidDataException("Invalid relative path in the export receipt.");
        var full = Normalize(Path.Combine(root, relative));
        if (!StrictlyWithin(full, root))
            throw new InvalidDataException("An export receipt points outside the output directory.");
        return full;
    }

    private static bool EnsureRegularOrMissing(string path)
    {
        var attrs = Attributes(path);
        if (attrs is null) return false;
        if ((attrs.Value & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("A receipt or track path is linked or not a regular file.");
        return true;
    }

    private static void EnsureDirectory(string path, bool create = false)
    {
        var chain = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            chain.Push(current);
        while (chain.TryPop(out var current))
        {
            var attrs = Attributes(current);
            if (attrs is null && create)
            {
                Directory.CreateDirectory(current);
                attrs = Attributes(current);
            }
            if (attrs is null || (attrs.Value & FileAttributes.Directory) == 0 ||
                (attrs.Value & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A directory required for a receipt is missing, linked, or not a directory.");
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Profile paths must be absolute.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsMacOS())
        {
            foreach (var alias in new[] { "/tmp", "/var" })
                if (AtOrWithin(full, alias) &&
                    string.Equals(Directory.ResolveLinkTarget(alias, returnFinalTarget: false)?.FullName,
                        "/private" + alias, StringComparison.Ordinal))
                    return "/private" + full;
        }
        return full;
    }

    private static bool AtOrWithin(string path, string root) =>
        string.Equals(path, root, PathComparison) || StrictlyWithin(path, root);

    private static bool StrictlyWithin(string path, string root) =>
        !string.Equals(path, root, PathComparison) &&
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);

    private static bool Overlaps(string left, string right)
    {
        // Reject alternate spellings too: a case-insensitive or Unicode-normalizing
        // volume must not let receipt writes reach source/output through another name.
        left = PathKey(left);
        right = PathKey(right);
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        static string Prefix(string path) => Path.EndsInDirectorySeparator(path)
            ? path : path + Path.DirectorySeparatorChar;
        return string.Equals(left, right, comparison) ||
            left.StartsWith(Prefix(right), comparison) || right.StartsWith(Prefix(left), comparison);
    }

    private static string PathKey(string path) => path.Normalize(NormalizationForm.FormKC);

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
