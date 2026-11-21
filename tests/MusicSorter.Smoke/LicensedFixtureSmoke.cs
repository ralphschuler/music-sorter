using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MusicSorter.Core;
using MusicSorter.Metadata;

internal static class LicensedFixtureSmoke
{
    private const double CandidateThreshold = 0.92;
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record Fixture(string Path, string Artist, string Title, string Genre = "", uint Year = 0);

    internal static async Task RunAsync(string manifestPath, bool liveLookup)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = budget.Token;
        manifestPath = Path.GetFullPath(manifestPath);
        var manifestDir = Path.GetDirectoryName(manifestPath)!;
        await using var manifest = File.OpenRead(manifestPath);
        var declared = await JsonSerializer.DeserializeAsync<Fixture[]>(manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web), ct) ?? [];
        Require(declared.Length is 4 or 5, "Use four or five full tracks in the creator-sourced manifest.");
        Require(declared.All(f => f is not null && !string.IsNullOrWhiteSpace(f.Path) &&
            !string.IsNullOrWhiteSpace(f.Artist) && !string.IsNullOrWhiteSpace(f.Title)),
            "Every fixture needs Path, Artist and Title.");
        var fixtures = declared.Select(f => f with { Path = Path.GetFullPath(f.Path, manifestDir) }).ToArray();
        Require(fixtures.Select(f => f.Path).Distinct(Paths).Count() == fixtures.Length,
            "Manifest entries must refer to different local files.");

        var temp = CanonicalTemp(Directory.CreateTempSubdirectory("music-sorter-licensed-").FullName);
        var oldProfiles = Environment.GetEnvironmentVariable("MUSIC_SORTER_PROFILE_DIR");
        var oldIndex = Environment.GetEnvironmentVariable("MUSIC_SORTER_INDEX_DIR");
        try
        {
            Environment.SetEnvironmentVariable("MUSIC_SORTER_PROFILE_DIR", Path.Combine(temp, "profiles"));
            Environment.SetEnvironmentVariable("MUSIC_SORTER_INDEX_DIR", Path.Combine(temp, "index"));
            var sources = new[] { Path.Combine(temp, "source-a"), Path.Combine(temp, "source-b") };
            var output = Path.Combine(temp, "output");
            foreach (var directory in sources)
                Directory.CreateDirectory(Path.Combine(directory, "nested"));

            var beforeDownload = new Dictionary<string, string>(Paths);
            var originals = new string[fixtures.Length];
            for (var i = 0; i < fixtures.Length; i++)
            {
                var source = fixtures[i].Path;
                Require(File.Exists(source) && new FileInfo(source).Length >= 100_000,
                    $"Fixture {i + 1} is missing or implausibly small for a full track.");
                var extension = Path.GetExtension(source);
                Require(extension.Length > 1, $"Fixture {i + 1} needs an audio extension.");
                beforeDownload.Add(source, await HashAsync(source, ct));
                var original = Path.Combine(sources[i % 2], "nested", $"licensed-original-{i + 1}{extension}");
                File.Copy(source, original, overwrite: false);
                originals[i] = original;
            }
            Require(beforeDownload.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() == fixtures.Length,
                "The downloaded tracks must have distinct original hashes.");

            var duplicate = Path.Combine(sources[1], $"zz-exact-duplicate{Path.GetExtension(originals[0])}");
            File.Copy(originals[0], duplicate, overwrite: false);
            var firstExt = Path.GetExtension(originals[0]).Equals(".flac", StringComparison.OrdinalIgnoreCase)
                ? ".mp3" : ".flac";
            var stripped = new[]
            {
                Path.Combine(sources[1], "nested", "zz-stripped-1" + firstExt),
                Path.Combine(sources[0], "nested", "zz-stripped-2.m4a")
            };
            await ReencodeWithoutTagsAsync(originals[0], stripped[0], ct);
            await ReencodeWithoutTagsAsync(originals[1], stripped[1], ct);

            var tags = new TagLibStore();
            var rows = (await Task.WhenAll(sources.Select(source =>
                SortEngine.ScanAsync(source, tags, cancellationToken: ct)))).SelectMany(result => result)
                .ToDictionary(track => track.Source, Paths);
            Require(rows.Count == fixtures.Length + 3, $"Expected {fixtures.Length + 3} indexed audio files, got {rows.Count}.");
            foreach (var original in originals.Select((path, i) => (path, i)))
                Require(rows[original.path].Sha256 == beforeDownload[fixtures[original.i].Path],
                    $"Source copy {original.i + 1} no longer matches the fixture.");
            Require(rows[duplicate].Sha256 == rows[originals[0]].Sha256, "Exact duplicate differs at the byte level.");
            for (var i = 0; i < stripped.Length; i++)
            {
                Require(rows[stripped[i]].Tags == new TrackTags(),
                    $"Re-encoded file {i + 1} still has an artist, title, genre or year.");
                Require(rows[stripped[i]].Sha256 != rows[originals[i]].Sha256,
                    $"Re-encoded file {i + 1} did not change its original byte hash.");
            }
            Console.WriteLine($"Licensed fixtures: {fixtures.Length} creator-sourced originals, two tagless transcodes and one exact duplicate; {rows.Count} tracks across two roots.");

            var fingerprinter = new ChromaprintFingerprinter();
            var fingerprints = new Dictionary<string, AudioFingerprint>(StringComparer.OrdinalIgnoreCase);
            var strippedFingerprints = new List<AudioFingerprint>();
            var originalFingerprints = new List<AudioFingerprint>();
            for (var i = 0; i < fixtures.Length; i++)
            {
                var candidate = await fingerprinter.ComputeAsync(originals[i], ct);
                Require(candidate.DurationSeconds >= 25, $"Fixture {i + 1} is too short for this full-track test.");
                originalFingerprints.Add(candidate);
                fingerprints[rows[originals[i]].Sha256] = candidate;
                if (i >= stripped.Length)
                    continue;
                var strippedFingerprint = await fingerprinter.ComputeAsync(stripped[i], ct);
                strippedFingerprints.Add(strippedFingerprint);
                fingerprints[rows[stripped[i]].Sha256] = strippedFingerprint;
                var score = FingerprintSimilarity.Score(candidate, strippedFingerprint);
                Require(score >= CandidateThreshold, $"Chromaprint score for derivative {i + 1} is {score:F3}, below candidate threshold {CandidateThreshold:F2}.");
                Console.WriteLine($"Chromaprint derivative {i + 1}: similarity {score:F3}, duration {candidate.DurationSeconds:F1}s, different SHA-256; review candidate only.");
            }
            Require(FingerprintSimilarity.Score(originalFingerprints[0], originalFingerprints[1]) < CandidateThreshold,
                "The first two distinct licensed recordings were not distinguishable at the candidate threshold.");

            foreach (var source in sources)
            {
                var scoped = rows.Values.Where(track => Within(track.Source, source)).ToArray();
                await TrackIndex.SaveWithFingerprintsAsync(source, scoped, fingerprints, ct);
                var reopened = await TrackIndex.LoadAsync(source, ct);
                Require(reopened is not null && scoped.All(track => reopened.Fingerprints.TryGetValue(track.Sha256, out var fp) &&
                    fp.Value == fingerprints[track.Sha256].Value && fp.Raw is { Length: > 0 }),
                    "Local index did not retain the original hash/fingerprint association.");
            }

            if (liveLookup)
                await TestLiveLookupAsync(fixtures[0], strippedFingerprints[0], ct);
            else
                Console.WriteLine("Live provider lookup not requested. Using verified creator metadata for reviewed fill.");

            // A deterministic review fixture: prefer nonempty canonical tags from the scanned
            // originals for consistent variant labels; fill only missing fields from the creator data.
            var byPath = new Dictionary<string, TrackTags>(Paths);
            for (var i = 0; i < stripped.Length; i++)
            {
                var fact = fixtures[i];
                var reviewed = FillOnlyMissing(rows[originals[i]].Tags,
                    new TrackTags(fact.Artist.Trim(), fact.Title.Trim(), fact.Genre?.Trim() ?? "", fact.Year));
                Require(reviewed.Artist.Length > 0 && reviewed.Title.Length > 0,
                    "Reviewed fixture metadata needs an artist and title.");
                byPath[originals[i]] = reviewed;
                byPath[stripped[i]] = reviewed;
                if (i == 0)
                    byPath[duplicate] = reviewed;
            }

            var effective = rows.Values.Select(track => byPath.TryGetValue(track.Source, out var approved)
                ? track with { Tags = FillOnlyMissing(track.Tags, approved) } : track).ToArray();
            var profile = new SortProfile(Guid.NewGuid(), "Licensed fixture test", sources, output, "{Artist}/{Year}/{Title}");
            SortEngine.ValidateProfileFolders(sources, output);
            await ProfileStore.SaveAsync(profile, ct);
            var saved = (await ProfileStore.LoadAsync(ct)).SingleOrDefault(item => item.Id == profile.Id);
            Require(saved is not null && saved.Sources.SequenceEqual(sources, Paths), "Saved multi-source profile did not reopen.");

            var plan = SortEngine.Plan(effective, output, profile.Template);
            Require(plan.Count(item => item.Status == PlanStatus.Duplicate) == 1, "Global plan did not skip exactly one byte duplicate.");
            Require(stripped.All(path => plan.Single(item => Paths.Equals(item.Track.Source, path)).Status == PlanStatus.Variant),
                "Matching tags and changed encoding did not remain distinct variants.");
            var first = await ProfileSorter.ExportAsync(profile, plan, tags, ct: ct);
            Require(first.Count(result => result.Outcome == "Copied") == fixtures.Length + 2 &&
                first.Count(result => result.Outcome == "SkippedDuplicate") == 1,
                $"Unexpected first export results: {Summarize(first)}.");

            foreach (var result in first.Where(item => item.Outcome == "Copied"))
            {
                var original = rows[result.Item.Track.Source];
                var exported = tags.Read(result.Item.Destination!);
                Require(exported == result.Item.Track.Tags,
                    $"Reopened copy's tags do not match reviewed tags: {Path.GetFileName(result.Item.Track.Source)}.");
                Require(FillOnlyMissing(original.Tags, exported) == exported, "Copy overwrote a populated source tag.");
            }
            var firstFiles = await OutputFilesAsync(output, ct);
            Require(firstFiles.Count == fixtures.Length + 2, "Output file count differs from verified copies.");

            var second = await ProfileSorter.ExportAsync(profile, plan, tags, ct: ct);
            Require(second.Count(result => result.Outcome == "AlreadyPresent") == fixtures.Length + 2 &&
                second.Count(result => result.Outcome == "SkippedDuplicate") == 1,
                $"Repeated sort did not recognize prior exports: {Summarize(second)}.");
            var secondFiles = await OutputFilesAsync(output, ct);
            Require(firstFiles.Count == secondFiles.Count && firstFiles.All(entry =>
                secondFiles.TryGetValue(entry.Key, out var hash) && hash == entry.Value),
                "Repeated sort changed an existing export or added another copy.");

            foreach (var track in rows.Values)
                Require(await HashAsync(track.Source, ct) == track.Sha256, "A test source file changed during the export.");
            foreach (var item in beforeDownload)
                Require(await HashAsync(item.Key, ct) == item.Value, "A downloaded creator fixture was modified.");
            Require(stripped.All(path => tags.Read(path) == new TrackTags()), "A source derivative gained tags after export.");
            Console.WriteLine($"Licensed fixture E2E passed: {fixtures.Length + 2} copies, reopened metadata checked, exact duplicate skipped, unchanged originals, no extra copies on rerun.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUSIC_SORTER_PROFILE_DIR", oldProfiles);
            Environment.SetEnvironmentVariable("MUSIC_SORTER_INDEX_DIR", oldIndex);
            Directory.Delete(temp, recursive: true);
        }
    }

    private static async Task TestLiveLookupAsync(Fixture fixture, AudioFingerprint fingerprint, CancellationToken ct)
    {
        try
        {
            var rows = await new MusicBrainzClient().LookupAsync(fixture.Artist, fixture.Title, durationSeconds: null, ct: ct);
            var verified = rows.Any(row => Same(row.Artist, fixture.Artist) && Same(row.Title, fixture.Title));
            Console.WriteLine($"MusicBrainz live: {rows.Count} candidate(s); " +
                (verified ? "artist/title matched the creator manifest." : "none was verified against the creator manifest."));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            Console.WriteLine($"MusicBrainz live: not verified ({TransportDiagnostic(ex)}).");
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACOUSTID_API_KEY")))
        {
            Console.WriteLine("AcoustID live: not tested (no application key in ACOUSTID_API_KEY).");
            return;
        }
        try
        {
            var rows = await new AcoustIdClient().LookupAsync(fingerprint, ct);
            var verified = rows.Any(row => Same(row.Artist, fixture.Artist) && Same(row.Title, fixture.Title));
            Console.WriteLine($"AcoustID live: {rows.Count} candidate(s); " +
                (verified ? "artist/title matched the creator manifest." : "none was verified against the creator manifest."));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            Console.WriteLine($"AcoustID live: not verified ({TransportDiagnostic(ex)}).");
        }
    }

    private static string TransportDiagnostic(Exception error) => error is HttpRequestException http
        ? (http.StatusCode is { } status ? $"HTTP {(int)status}" : $"{http.HttpRequestError}, {http.InnerException?.GetType().Name ?? "no inner error"}")
        : error.GetType().Name;

    private static async Task ReencodeWithoutTagsAsync(string input, string output, CancellationToken ct)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("MUSIC_SORTER_FFMPEG");
        var options = new ProcessStartInfo(string.IsNullOrWhiteSpace(ffmpeg) ? "ffmpeg" : ffmpeg)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", input, "-map", "0:a:0",
            "-map_metadata", "-1", "-map_chapters", "-1", "-vn", "-n" })
            options.ArgumentList.Add(argument);
        var codec = Path.GetExtension(output) switch { ".mp3" => "libmp3lame", ".m4a" => "aac", _ => "flac" };
        options.ArgumentList.Add("-c:a");
        options.ArgumentList.Add(codec);
        if (codec != "flac")
        {
            options.ArgumentList.Add("-b:a");
            options.ArgumentList.Add("160k");
        }
        options.ArgumentList.Add(output);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(options) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errorTask = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), outputTask, errorTask);
            if (process.ExitCode != 0)
                throw new IOException($"ffmpeg re-encoding failed (exit {process.ExitCode}): {errorTask.Result.Trim()}");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static TrackTags FillOnlyMissing(TrackTags old, TrackTags proposal) => new(
        string.IsNullOrWhiteSpace(old.Artist) ? proposal.Artist : old.Artist,
        string.IsNullOrWhiteSpace(old.Title) ? proposal.Title : old.Title,
        string.IsNullOrWhiteSpace(old.Genre) ? proposal.Genre : old.Genre,
        old.Year == 0 ? proposal.Year : old.Year);

    private static async Task<Dictionary<string, string>> OutputFilesAsync(string folder, CancellationToken ct)
    {
        var rows = new Dictionary<string, string>(Paths);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            rows.Add(Path.GetRelativePath(folder, path), await HashAsync(path, ct));
        return rows;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
    }

    private static string CanonicalTemp(string path) => OperatingSystem.IsMacOS() &&
        (path.StartsWith("/var/", StringComparison.Ordinal) || path.StartsWith("/tmp/", StringComparison.Ordinal))
        ? "/private" + path : path;

    private static bool Within(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Same(string left, string right) => left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string Summarize(IEnumerable<ProfileExportResult> results) =>
        string.Join("; ", results.Select(item => $"{item.Outcome}{(item.Message is null ? "" : ": " + item.Message)}"));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
