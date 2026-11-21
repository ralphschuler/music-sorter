using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MusicSorter.Metadata;

public sealed record AcoustIdCandidate(string AcoustId, string RecordingId, string Artist, string Title, double Score);

/// <summary>User-triggered review suggestions. Sends only derived fingerprint and duration, never audio or a path.</summary>
public sealed class AcoustIdClient
{
    private static readonly HttpClient DefaultClient = LookupTransport.Create();
    private static readonly LookupTransport RateLimit = new();
    private readonly HttpClient client;
    private readonly Func<string?> key;

    internal AcoustIdClient(HttpClient client, Func<string?> key)
    {
        this.client = client;
        this.key = key;
    }

    public AcoustIdClient() : this(DefaultClient, () => Environment.GetEnvironmentVariable("ACOUSTID_API_KEY")) { }

    public async Task<IReadOnlyList<AcoustIdCandidate>> LookupAsync(AudioFingerprint fingerprint,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(fingerprint);
        var appKey = key();
        if (string.IsNullOrWhiteSpace(appKey))
            throw new InvalidOperationException("AcoustID needs an application key in ACOUSTID_API_KEY.");
        if (!double.IsFinite(fingerprint.DurationSeconds) || fingerprint.DurationSeconds is < 1 or > 86_400 ||
            fingerprint.Value is not { Length: > 0 and <= 65_536 } ||
            fingerprint.Value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '='))
            throw new ArgumentException("AcoustID lookup needs an encoded Chromaprint value and valid duration.", nameof(fingerprint));
        var duration = Math.Round(fingerprint.DurationSeconds, MidpointRounding.AwayFromZero);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.acoustid.org/v2/lookup");
        request.Headers.UserAgent.ParseAdd("MusicSorter/0.1 (+https://github.com/ralphschuler/music-sorter/issues)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client"] = appKey,
            ["duration"] = duration.ToString(CultureInfo.InvariantCulture),
            ["fingerprint"] = fingerprint.Value,
            ["meta"] = "recordings",
            ["format"] = "json"
        });
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await RateLimit.SendAsync(client, request, budget.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: budget.Token).ConfigureAwait(false);
        if (MusicBrainzClient.ReadText(document.RootElement, "status") != "ok" ||
            !document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AcoustID did not return usable results.");

        var candidates = new List<AcoustIdCandidate>();
        foreach (var match in results.EnumerateArray())
        {
            var acoustId = MusicBrainzClient.ReadText(match, "id");
            if (!Guid.TryParse(acoustId, out _) ||
                !match.TryGetProperty("score", out var confidence) ||
                confidence.ValueKind != JsonValueKind.Number || !confidence.TryGetDouble(out var score) ||
                !double.IsFinite(score) || score is < 0 or > 1)
                continue;
            if (!match.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var recording in recordings.EnumerateArray())
            {
                var id = MusicBrainzClient.ReadText(recording, "id");
                if (!Guid.TryParse(id, out _))
                    continue;
                var artist = recording.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", artists.EnumerateArray().Select(a => MusicBrainzClient.ReadText(a, "name"))
                        .Where(name => name.Length > 0))
                    : "";
                candidates.Add(new AcoustIdCandidate(acoustId, id, artist,
                    MusicBrainzClient.ReadText(recording, "title"), score));
            }
        }
        return candidates.OrderByDescending(candidate => candidate.Score).DistinctBy(candidate => candidate.RecordingId)
            .Take(6).ToArray();
    }
}
