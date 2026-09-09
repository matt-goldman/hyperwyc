using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using System.Text;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// A cached response is replaced when the resource is refetched, not stacked beside the
/// previous one.
/// </summary>
/// <remarks>
/// Cache entries used to be created with a fresh <c>Guid</c> like any other record, and the
/// store keyed on that id — so every refetch inserted rather than replaced, and
/// <c>GetCachedResponseAsync</c>'s <c>FirstOrDefault</c> kept returning the oldest. The cache
/// froze at the first response ever stored and the store grew by an unreadable record per
/// refetch. Found in the sample: recording a sale online updated the quantities on screen, then
/// going offline showed the original ones again. The entry is now keyed by the URL it caches,
/// which is the same fix stated as identity rather than as an id convention.
/// </remarks>
public class CacheRefreshTests
{
    private const string Url = "https://example.com/api/products";

    /// <summary>Returns a different body each call, so a stale read is unmistakable.</summary>
    private sealed class VersioningTransport : HttpMessageHandler
    {
        public int Version;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"stock-v{Interlocked.Increment(ref Version)}", Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>
    /// A zero TTL means every read is a refetch, which is what exercises the replace. Offline
    /// it would also mean nothing is servable, so the offline handler gets a live TTL.
    /// </summary>
    private static HyperwycHandler Handler(
        IHyperwycStore store, HttpMessageHandler inner, bool connected = true) =>
        new(store,
            new FakeConnectivityService(connected),
            new HyperwycEventStream(),
            TestOptions.WithTtl(connected ? TimeSpan.Zero : TimeSpan.FromMinutes(5)), TestHealth())
        { InnerHandler = inner };

    [Fact]
    public async Task Refetching_ReplacesTheCachedResponse()
    {
        var store = new InMemoryStore();
        var transport = new VersioningTransport();

        using (var client = new HttpClient(Handler(store, transport)))
        {
            await client.GetAsync(Url);
            await client.GetAsync(Url);
            await client.GetAsync(Url);
        }

        var cached = await store.GetCachedResponseAsync(Url);

        Assert.Equal(3, transport.Version);
        Assert.Equal("stock-v3", cached?.GetBodyAsText());
    }

    [Fact]
    public async Task Refetching_DoesNotAccumulateCacheEntries()
    {
        var store = new CountingStore();

        using (var client = new HttpClient(Handler(store, new VersioningTransport())))
            for (var i = 0; i < 5; i++)
                await client.GetAsync(Url);

        // Nothing queued, and exactly one cache entry in the store — not five.
        Assert.Empty(await store.GetPendingOutboxAsync());
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task GoingOfflineAfterARefresh_ServesTheRefreshedCopy()
    {
        // The sample's scenario: read online twice, then read offline.
        var store = new InMemoryStore();
        var transport = new VersioningTransport();

        using (var online = new HttpClient(Handler(store, transport)))
        {
            await online.GetAsync(Url);
            await online.GetAsync(Url);
        }

        using var offline = new HttpClient(Handler(
            store,
            new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            connected: false));

        var response = await offline.GetAsync(Url);

        Assert.Equal("stock-v2", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DifferentUrls_KeepSeparateCacheEntries()
    {
        var store = new CountingStore();

        using (var client = new HttpClient(Handler(store, new VersioningTransport())))
        {
            await client.GetAsync(Url);
            await client.GetAsync("https://example.com/api/sales");
        }

        Assert.Equal(2, store.Count);
        Assert.NotNull(await store.GetCachedResponseAsync(Url));
        Assert.NotNull(await store.GetCachedResponseAsync("https://example.com/api/sales"));
    }

    [Fact]
    public async Task QueuedWritesToACachedUrl_AreNotDisturbed()
    {
        // The two kinds are different types in different partitions of the store, so a cached
        // response and a queued write to the same URL cannot disturb each other.
        var store = new InMemoryStore();

        using (var offline = new HttpClient(Handler(
            store, new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)), connected: false)))
        {
            await offline.PostAsync(Url, new StringContent("one"));
            await offline.PostAsync(Url, new StringContent("two"));
        }

        using (var online = new HttpClient(Handler(store, new VersioningTransport())))
            await online.GetAsync(Url);

        var queued = await store.GetPendingOutboxAsync();
        Assert.Equal(2, queued.Count);
        Assert.NotNull(await store.GetCachedResponseAsync(Url));
    }

    /// <summary>
    /// Delegates to an <see cref="InMemoryStore"/> while tracking the URLs it has been asked
    /// to cache, so a test can see how many entries actually exist rather than only what the
    /// query methods choose to return. Accumulation is otherwise invisible: a shadowed cache
    /// entry is returned by nothing.
    /// </summary>
    private sealed class CountingStore : IHyperwycStore
    {
        private readonly InMemoryStore _inner = new();
        private readonly HashSet<string> _cached = [];

        public int Count => _cached.Count;

        public Task PutCachedResponseAsync(CachedResponse response, CancellationToken ct = default)
        {
            _cached.Add(response.Url);
            return _inner.PutCachedResponseAsync(response, ct);
        }

        public Task<CachedResponse?> GetCachedResponseAsync(string url, CancellationToken ct = default) =>
            _inner.GetCachedResponseAsync(url, ct);

        public Task<IReadOnlyList<QueuedWrite>> GetPendingOutboxAsync(CancellationToken ct = default) =>
            _inner.GetPendingOutboxAsync(ct);

        public Task UpsertQueuedWriteAsync(QueuedWrite write, CancellationToken ct = default) =>
            _inner.UpsertQueuedWriteAsync(write, ct);

        public Task RemoveDeliveredAsync(string id, CancellationToken ct = default) =>
            _inner.RemoveDeliveredAsync(id, ct);

        public Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default) =>
            _inner.InvalidateCacheForPrefixAsync(urlPrefix, ct);

        public Task ResetAsync(CancellationToken ct = default)
        {
            _cached.Clear();
            return _inner.ResetAsync(ct);
        }
    }
}
