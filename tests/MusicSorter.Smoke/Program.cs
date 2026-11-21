using MusicSorter.Core;
using MusicSorter.Metadata;
using System.Security.Cryptography;

using System.Net;

if (Array.IndexOf(args, "--licensed-fixtures") is var fixtureIndex && fixtureIndex >= 0)
{
    if (fixtureIndex + 1 == args.Length) throw new ArgumentException("Pass a local fixture manifest after --licensed-fixtures.");
    await LicensedFixtureSmoke.RunAsync(args[fixtureIndex + 1], args.Contains("--live-lookup", StringComparer.Ordinal));
    return;
}

var root = Directory.CreateTempSubdirectory("musicsorter-smoke-");
try
{
    var source = Directory.CreateDirectory(Path.Combine(root.FullName, "source")).FullName;
    var sub = Directory.CreateDirectory(Path.Combine(source, "sub")).FullName;
    var target = Path.Combine(root.FullName, "target");
    var a = Path.Combine(source, "a.wav");
    var b = Path.Combine(sub, "b.wav");
    var c = Path.Combine(source, "c.wav");
    WriteWave(a, 220);
    File.Copy(a, b);
    WriteWave(c, 440);
    File.WriteAllText(Path.Combine(source, "ignore.txt"), "not audio");
    var readback = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a)));

    var store = new TestTags();
    var tracks = await SortEngine.ScanAsync(source, store);
    Assert(tracks.Count == 3, "recursive scan processes three audio files");
    Assert(tracks.Single(x => x.Source == a).Sha256.Equals(readback, StringComparison.OrdinalIgnoreCase), "hash is SHA-256 of source bytes");
    var planned = SortEngine.Plan(tracks, target, "{artistInitial}/{artist}/{title}");
    Assert(planned.Count(p => p.Status == PlanStatus.Duplicate && p.Destination is null) == 1, "identical bytes collapse even under different filenames");
    var exports = planned.Where(p => p.Destination is not null).ToArray();
    Assert(exports.Length == 2 && exports.Any(p => p.Status == PlanStatus.Variant), "different bytes with the same tags remain as variants");
    Assert(exports.Select(p => p.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "variants never collide under a shared schema");
    Assert(!Directory.Exists(target), "preview writes nothing to the output");
    var unknown = tracks.Select(t => t with { Tags = new TrackTags() });
    var unknownPlan = SortEngine.Plan(unknown, target, "{fileName}");
    Assert(!unknownPlan.Any(p => p.Status == PlanStatus.Variant), "empty tags do not imply a matching song");
    await SortEngine.ExportAsync(source, planned, store);
    Assert(Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length == 2, "copy exports only the distinct inputs");
    Assert(File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)), "sources remain untouched");
    var existing = exports[0].Destination!;
    File.WriteAllText(existing, "sentinel");
    try { await SortEngine.ExportAsync(source, planned, store); }
    catch (IOException) { } // A collision can be reported as a refusal.
    Assert(File.ReadAllText(existing) == "sentinel", "subsequent export never overwrites an existing destination");

    var realTags = new TagLibStore();
    var tagFixture = Path.Combine(root.FullName, "tag-fixture.wav");
    File.Copy(a, tagFixture);
    realTags.FillMissing(tagFixture, new TrackTags("Sample Artist", "Sample Title", "Sample Genre", 2024));
    realTags.FillMissing(tagFixture, new TrackTags("Do Not Replace", "Do Not Replace", "Do Not Replace", 2025));
    var actual = realTags.Read(tagFixture);
    Assert(actual is { Artist: "Sample Artist", Title: "Sample Title", Genre: "Sample Genre", Year: 2024 },
        "real tags round-trip and populated fields are preserved");
    var mismatchTarget = Path.Combine(root.FullName, "changed-source");
    var mismatchPlan = SortEngine.Plan([tracks.Single(x => x.Source == a)], mismatchTarget, "{fileName}");
    File.AppendAllText(a, "changed after scan");
    try { await SortEngine.ExportAsync(source, mismatchPlan, store); }
    catch (IOException) { }
    Assert(!Directory.Exists(mismatchTarget) || Directory.GetFiles(mismatchTarget, "*", SearchOption.AllDirectories).Length == 0,
        "changed source is refused and no temporary output is left");
    File.Copy(b, a, overwrite: true);

    using var musicHttp = new HttpClient(new OfflineHandler(async (request, token) =>
    {
        var uri = Uri.UnescapeDataString(request.RequestUri!.Query);
        Assert(request.RequestUri.Host == "musicbrainz.org" && uri.Contains("Test Artist") &&
            uri.Contains("Test Title") && !uri.Contains(source), "MusicBrainz receives only catalog fields");
        await Task.CompletedTask;
        return """{"recordings":[{"id":"abc83ccb-10a2-49ce-be9c-eafbcfe0c714","title":"Test Title","first-release-date":"2024-02-03","score":95,"length":15000,"artist-credit":[{"name":"Test Artist","joinphrase":""}]}]}""";
    }));
    var music = await new MusicBrainzClient(musicHttp).LookupAsync("Test Artist", "Test Title", 15);
    Assert(music.Single() is { Artist: "Test Artist", Title: "Test Title", Year: 2024 },
        "MusicBrainz response becomes a review candidate");

    using var acoustIdHttp = new HttpClient(new OfflineHandler(async (request, token) =>
    {
        var payload = await request.Content!.ReadAsStringAsync(token);
        Assert(request.Method == HttpMethod.Post && request.RequestUri!.Host == "api.acoustid.org" &&
            payload.Contains("duration=15") && payload.Contains("fingerprint=AQABsynthetic_") &&
            !payload.Contains("42424242") && !payload.Contains(source),
            "AcoustID receives only its key and fingerprint lookup data, no audio or raw local words");
        return """{"status":"ok","results":[{"id":"d89766e1-6c92-478f-9f03-00bff72b22bc","score":0.95,"recordings":[{"id":"abc83ccb-10a2-49ce-be9c-eafbcfe0c714","title":"Test Title","artists":[{"name":"Test Artist"}]}]}]}""";
    }));
    var acoustId = await new AcoustIdClient(acoustIdHttp, () => "synthetic-app-key")
        .LookupAsync(new AudioFingerprint("AQABsynthetic_", 15) { Raw = [42, 42424242] });
    Assert(acoustId.Single() is { Artist: "Test Artist", Title: "Test Title", Score: .95 },
        "AcoustID response becomes a review candidate");

    var secondSource = Directory.CreateDirectory(Path.Combine(root.FullName, "source-2")).FullName;
    File.Copy(a, Path.Combine(secondSource, "exact.wav"));
    WriteWave(Path.Combine(secondSource, "variant.wav"), 330);
    var profileOutput = Path.Combine(root.FullName, "profile-output");
    var profile = new SortProfile(Guid.NewGuid(), "Two inputs", [source, secondSource], profileOutput,
        "{artistInitial}/{artist}/{title}");
    Environment.SetEnvironmentVariable("MUSIC_SORTER_PROFILE_DIR", Path.Combine(root.FullName, "profiles"));
    Environment.SetEnvironmentVariable("MUSIC_SORTER_INDEX_DIR", Path.Combine(root.FullName, "index"));
    SortEngine.ValidateProfileFolders(profile.Sources, profile.Output);
    await ProfileStore.SaveAsync(profile);
    await ProfileStore.SaveAsync(profile with { Name = "My test profile" });
    var loaded = (await ProfileStore.LoadAsync()).Single();
    Assert(loaded is { Name: "My test profile" } && loaded.Sources.SequenceEqual(profile.Sources) &&
        loaded.Output == profile.Output && loaded.Template == profile.Template, "profile update round-trips both inputs, one output and its pattern");
    try
    {
        SortEngine.ValidateProfileFolders([source, sub], profileOutput);
        Assert(false, "nested input paths must be rejected");
    }
    catch (Exception error) when (error is IOException or ArgumentException)
    {
        Assert(true, "nested input paths are rejected");
    }
    var allTracks = tracks.Concat(await SortEngine.ScanAsync(secondSource, store));
    var profilePlan = SortEngine.Plan(allTracks, profile.Output, profile.Template);
    Assert(profilePlan.Count(item => item.Status == PlanStatus.Duplicate) == 2 &&
        profilePlan.Count(item => item.Status == PlanStatus.Variant) == 3, "SHA-256 deduplication spans both folders and keeps three variants");
    Assert(!Directory.Exists(profileOutput), "a profile and its preview do not create the output");
    var conflict = profilePlan.First(item => item.Destination is not null).Destination!;
    Directory.CreateDirectory(Path.GetDirectoryName(conflict)!);
    File.WriteAllText(conflict, "foreign contents");
    var profileExport = await SortEngine.ExportProfileAsync(profile.Sources, profilePlan, store);
    Assert(profileExport.Count(x => x.Outcome == "Conflict") == 1 &&
        profileExport.Count(x => x.Outcome == "Copied") == 2 &&
        profileExport.Count(x => x.Outcome == "SkippedDuplicate") == 2 &&
        File.ReadAllText(conflict) == "foreign contents", "multi-source export continues after a conflict and never overwrites");
    var repeatedExport = await SortEngine.ExportProfileAsync(profile.Sources, profilePlan, store);
    Assert(repeatedExport.Count(x => x.Outcome == "AlreadyPresent") == 2,
        "an unchanged repeated profile export skips already present original bytes");

    var receiptOutput = Path.Combine(root.FullName, "receipt-output");
    var receiptProfile = profile with { Id = Guid.NewGuid(), Output = receiptOutput };
    var firstTrack = tracks.Single(t => t.Source == a);
    var addedVariant = allTracks.Single(t => t.Source.EndsWith("variant.wav"));
    var firstPlan = SortEngine.Plan([firstTrack], receiptOutput, profile.Template);
    var firstExport = await ProfileSorter.ExportAsync(receiptProfile, firstPlan, store);
    Assert(firstExport.Single().Outcome == "Copied", "initial sort publishes the first tracked copy");
    var laterPlan = SortEngine.Plan([firstTrack, addedVariant], receiptOutput, profile.Template);
    Assert(laterPlan.Single(item => item.Track.Source == a).Destination != firstPlan.Single().Destination,
        "adding a variant changes the old track's suggested destination");
    var laterExport = await ProfileSorter.ExportAsync(receiptProfile, laterPlan, store);
    Assert(laterExport.Count(item => item.Outcome == "Copied") == 1 &&
        laterExport.Count(item => item.Outcome == "AlreadyPresent") == 1 &&
        Directory.GetFiles(receiptOutput, "*", SearchOption.AllDirectories).Length == 2,
        "a valid prior receipt prevents a duplicate after adding a variant");

    var indexProgress = System.Threading.Channels.Channel.CreateUnbounded<ProfileIndexUpdate>();
    await using (var session = new ProfileIndexSession(loaded, store,
        new TestProgress<ProfileIndexUpdate>(update => indexProgress.Writer.TryWrite(update))))
    {
        session.Start();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (true)
        {
            var update = await indexProgress.Reader.ReadAsync(wait.Token);
            if (!update.IsRefreshing && update.Tracks.Count == 5) break;
        }
        WriteWave(Path.Combine(secondSource, "added-live.wav"), 550);
        while (true)
        {
            var update = await indexProgress.Reader.ReadAsync(wait.Token);
            if (!update.IsRefreshing && update.Tracks.Count == 6 && update.Tracks.Any(t => t.Source.EndsWith("added-live.wav"))) break;
        }
        Assert(true, "open profile refreshes automatically when a file appears in its second input");
    }

    if (args.Contains("--fingerprint", StringComparer.Ordinal))
    {
        var tool = new ChromaprintFingerprinter();
        var first = await tool.ComputeAsync(a);
        var second = await tool.ComputeAsync(b);
        var third = await tool.ComputeAsync(c);
        Assert(first is not null && second is not null && third is not null, "fingerprinting synthetic WAV files works locally");
        Assert(first!.Value == second!.Value && first.DurationSeconds > 10, "same audio yields the same stored fingerprint");
        Assert(first.Value != third!.Value, "different synthetic audio produces a different fingerprint");
        var changedMetadata = await tool.ComputeAsync(tagFixture);
        Assert(!File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(tagFixture)) &&
            FingerprintSimilarity.Score(first, changedMetadata) >= .95,
            "same audio with different container metadata is a possible fingerprint match, not a byte match");
        Environment.SetEnvironmentVariable("MUSIC_SORTER_INDEX_DIR", Path.Combine(root.FullName, "index"));
        var fingerprintMap = new Dictionary<string, AudioFingerprint>
        {
            [tracks.Single(t => t.Source == a).Sha256] = first
        };
        await TrackIndex.SaveWithFingerprintsAsync(source, tracks, fingerprintMap);
        var snapshot = await TrackIndex.LoadAsync(source);
        Assert(snapshot?.Tracks.Count == 3 && snapshot.Fingerprints.Single().Value.Value == first.Value,
            "the local track index persists and reloads fingerprints keyed by original SHA-256");
    }
}
finally { root.Delete(recursive: true); }

static void Assert(bool pass, string message)
{
    if (!pass) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

static void WriteWave(string path, int baseFrequency)
{
    const int rate = 11025;
    const int samples = rate * 15;
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
    writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
    writer.Write("data"u8); writer.Write(samples * 2);
    for (var i = 0; i < samples; i++)
    {
        var t = (double)i / rate;
        var note = (int)(t * (baseFrequency == 220 ? 4 : 5));
        var f = baseFrequency * Math.Pow(2, ((note * (baseFrequency == 220 ? 7 : 3)) % 12) / 12.0);
        writer.Write((short)(9000 * Math.Sin(2 * Math.PI * t * f) + 5000 * Math.Sin(2 * Math.PI * t * f * 1.5)));
    }
}

sealed class TestTags : ITagStore
{
    public TrackTags Read(string path) => new("Artist", "Title", "Genre", 2026);
    public void FillMissing(string path, TrackTags proposed) { }
}


sealed class OfflineHandler(Func<HttpRequestMessage, CancellationToken, Task<string>> fixture) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        new(HttpStatusCode.OK) { Content = new StringContent(await fixture(request, cancellationToken)) };
}

sealed class TestProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
