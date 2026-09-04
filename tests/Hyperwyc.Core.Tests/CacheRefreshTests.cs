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
/// Cache envelopes used to be created with a fresh <c>Guid</c> like any other envelope, and
/// <c>UpsertAsync</c> keys on that id — so every refetch inserted rather than replaced, and
/// <c>GetCachedResponseAsync</c>'s <c>FirstOrDefault</c> kept returning the oldest. The cache
/// froze at the first response ever stored and the store grew by an unreadable envelope per
/// refetch. Found in the sample: recording a sale online updated the quantities on screen, then
/// going offline showed the original ones again.
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
        ISyncStore store, HttpMessageHandler inner, bool connected = true) =>
        new(store,
            new FakeConnectivityService(connected),
            new SyncEventStream(),
            TestOptions.WithTtl(connected ? TimeSpan.Zero : TimeSpan.FromMinutes(5)))
        { InnerHandler = inner };

    [Fact]
    public async Task Refetching_ReplacesTheCachedResponse()
    {
        var store = new InMemorySyncStore();
        var transport = new VersioningTransport();

        using (var client = new HttpClient(Handler(store, transport)))
        {
            await client.GetAsync(Url);
            await client.GetAsync(Url);
            await client.GetAsync(Url);
        }

        var cached = await store.GetCachedResponseAsync(Url);

        Assert.Equal(3, transport.Version);
        Assert.Equal("stock-v3", cached?.Response?.GetBodyAsText());
    }

    [Fact]
    public async Task Refetching_DoesNotAccumulateEnvelopes()
    {
        var store = new CountingStore();

        using (var client = new HttpClient(Handler(store, new VersioningTransport())))
            for (var i = 0; i < 5; i++)
                await client.GetAsync(Url);

        // Nothing queued, and exactly one envelope in the store — not five.
        Assert.Empty(await store.GetPendingOutboxAsync());
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task GoingOfflineAfterARefresh_ServesTheRefreshedCopy()
    {
        // The sample's scenario: read online twice, then read offline.
        var store = new InMemorySyncStore();
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
        // Cache ids are prefixed, so they can never collide with a queued write's random id —
        // even when the write targets a URL that is also cached.
        var store = new InMemorySyncStore();

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
    /// Delegates to an <see cref="InMemorySyncStore"/> while tracking the ids it has been asked
    /// to store, so a test can see how many envelopes actually exist rather than only what the
    /// query methods choose to return. Accumulation is otherwise invisible: an orphaned cache
    /// envelope is excluded from the outbox and shadowed in the cache lookup.
    /// </summary>
    private sealed class CountingStore : ISyncStore
    {
        private readonly InMemorySyncStore _inner = new();
        private readonly HashSet<string> _ids = [];

        public int Count => _ids.Count;

        public Task UpsertAsync(Envelope envelope, CancellationToken ct = default)
        {
            _ids.Add(envelope.Id);
            return _inner.UpsertAsync(envelope, ct);
        }

        public Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default) =>
            _inner.GetCachedResponseAsync(url, ct);

        public Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default) =>
            _inner.GetPendingOutboxAsync(ct);

        public Task MarkSyncedAsync(string id, CancellationToken ct = default) =>
            _inner.MarkSyncedAsync(id, ct);

        public Task MoveToDeadLetterAsync(string id, CancellationToken ct = default) =>
            _inner.MoveToDeadLetterAsync(id, ct);

        public Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default) =>
            _inner.InvalidateCacheForPrefixAsync(urlPrefix, ct);

        public Task ResetAsync(CancellationToken ct = default)
        {
            _ids.Clear();
            return _inner.ResetAsync(ct);
        }
    }
}
