using System.Net.Http;

namespace WindowsDynamicBar.Core.Net;

/// <summary>The app's one HttpClient (weather, lyrics, speed test, update check). Connections are
/// pooled and recycled every few minutes so DNS changes are picked up. No client-wide timeout:
/// every caller passes a CancellationToken with its own deadline.</summary>
public static class SharedHttp
{
    public static HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "WindowsDynamicBar" } },
    };

    /// <summary>A token that cancels after <paramref name="timeout"/> or when <paramref name="outer"/> does.</summary>
    public static CancellationTokenSource Deadline(TimeSpan timeout, CancellationToken outer = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        return cts;
    }
}
