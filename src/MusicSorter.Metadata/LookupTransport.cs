using System.Diagnostics;
using System.Net;

namespace MusicSorter.Metadata;

internal sealed class LookupTransport
{
    internal static HttpClient Create() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 1_048_576
    };

    private readonly SemaphoreSlim serial = new(1, 1);
    private long lastRequest;

    internal async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken ct)
    {
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (lastRequest != 0)
            {
                var delay = TimeSpan.FromMilliseconds(1100) - Stopwatch.GetElapsedTime(lastRequest);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            lastRequest = Stopwatch.GetTimestamp();
            return await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
        }
        finally { serial.Release(); }
    }
}
