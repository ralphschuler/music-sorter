using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MusicSorter.Metadata;

public sealed record MusicBrainzCandidate(string RecordingId, string Artist, string Title, uint Year,
    int Score, int? DurationMilliseconds);

/// <summary>Remote, user-triggered review suggestions. Never calls TagLib or updates files.</summary>
public sealed class MusicBrainzClient
{
    private static readonly HttpClient DefaultClient = LookupTransport.Create();
    private static readonly LookupTransport RateLimit = new();
    private readonly HttpClient client;

    internal MusicBrainzClient(HttpClient client) => this.client = client;

    public MusicBrainzClient() : this(DefaultClient) { }

    public async Task<IReadOnlyList<MusicBrainzCandidate>> LookupAsync(string artist, string title,
        CancellationToken ct) => await LookupAsync(artist, title, null, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<MusicBrainzCandidate>> LookupAsync(string artist, string title,
        double? durationSeconds = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        artist = LimitMetadata(artist);
        title = LimitMetadata(title);
        if (artist.Length == 0 && title.Length == 0)
            return [];
        var expressions = new List<string>();
        if (artist.Length > 0)
            expressions.Add("artistname:\"" + EscapeLiteral(artist) + "\"");
        if (title.Length > 0)
            expressions.Add("recording:\"" + EscapeLiteral(title) + "\"");
        if (durationSeconds is double seconds && double.IsFinite(seconds) && seconds is > 2 and < 86_400)
        {
            var low = ((long)((seconds - 2) * 1000)).ToString(CultureInfo.InvariantCulture);
            var high = ((long)((seconds + 2) * 1000)).ToString(CultureInfo.InvariantCulture);
            expressions.Add($"dur:[{low} TO {high}]");
        }
        var query = Uri.EscapeDataString(string.Join(" AND ", expressions));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://musicbrainz.org/ws/2/recording/?fmt=json&limit=6&query={query}");
        request.Headers.UserAgent.ParseAdd("MusicSorter/0.1 (+https://github.com/ralphschuler/music-sorter/issues)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await RateLimit.SendAsync(client, request, budget.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: budget.Token).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("MusicBrainz did not return a recording list.");

        var results = new List<MusicBrainzCandidate>();
        foreach (var item in recordings.EnumerateArray().Take(6))
        {
            var id = ReadText(item, "id");
            if (!Guid.TryParse(id, out _))
                continue;
            var firstRelease = ReadText(item, "first-release-date");
            var year = firstRelease.Length >= 4 && uint.TryParse(firstRelease.AsSpan(0, 4),
                NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            var credit = item.TryGetProperty("artist-credit", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? string.Concat(artists.EnumerateArray().Select(a => ReadText(a, "name") + ReadText(a, "joinphrase")))
                : "";
            var score = ReadInteger(item, "score") ?? 0;
            results.Add(new MusicBrainzCandidate(id, credit, ReadText(item, "title"), year,
                score, ReadInteger(item, "length")));
        }
        return results;
    }

    private static string LimitMetadata(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Lookup accepts short artist/title text only.");
        return value;
    }

    private static string EscapeLiteral(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    internal static string ReadText(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int? ReadInteger(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }
}
