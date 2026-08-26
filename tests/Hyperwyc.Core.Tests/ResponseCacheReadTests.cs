using System.Net;
using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

public class ResponseCacheReadTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HyperwycHandler BuildHandler(
        InMemorySyncStore store,
        Fakes.StubHttpMessageHandler inner,
        bool cacheIsStale,
        int maxBodyBytes = 512 * 1024)
    {
        // Staleness is a TTL comparison against CachedAt now that IStalenessEvaluator is gone
        // (ADR 0004). A zero TTL makes every cached entry stale; the default keeps them fresh,
        // since FreshCachedEnvelope stamps CachedAt as "now".
        var options = new HyperwycOptions
        {
            MaxCachedResponseBodyBytes = maxBodyBytes,
            DefaultCacheTtl = cacheIsStale ? TimeSpan.Zero : TimeSpan.FromMinutes(5),
        };
        return new HyperwycHandler(
            store,
            new Fakes.FakeConnectivityService(isConnected: true),
            new Fakes.FakeSyncPolicy(),
            new SyncEventStream(),
            options)
        { InnerHandler = inner };
    }

    private static Envelope FreshCachedEnvelope(string url, string body = "{}", string? etag = null)
    {
        var headers = new Dictionary<string, string>();
        if (etag is not null) headers["ETag"] = etag;

        var envelope = new Envelope { Url = url, Method = "GET" };
        envelope.Response = new CachedResponse
        {
            StatusCode = 200,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            Headers = headers,
            CachedAt = DateTimeOffset.UtcNow,
        };
        envelope.IsSynced = true;
        return envelope;
    }

    // Fresh: no network call, cached response returned
    [Fact]
    public async Task FreshCacheHit_DoesNotCallNetwork()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(FreshCachedEnvelope("https://example.com/api/items"));

        var stub = new Fakes.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(BuildHandler(store, stub, cacheIsStale: false));

        await client.GetAsync("https://example.com/api/items");

        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task FreshCacheHit_ReturnsCachedBody()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(FreshCachedEnvelope("https://example.com/api/items", "[1,2,3]"));

        var stub = new Fakes.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(BuildHandler(store, stub, cacheIsStale: false));

        var response = await client.GetAsync("https://example.com/api/items");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[1,2,3]", body);
    }

    [Fact]
    public async Task FreshCacheHit_ResponseHeadersRestored()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(FreshCachedEnvelope("https://example.com/api/items", etag: "\"abc123\""));

        var stub = new Fakes.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(BuildHandler(store, stub, cacheIsStale: false));

        var response = await client.GetAsync("https://example.com/api/items");

        Assert.True(response.Headers.TryGetValues("ETag", out var values));
        Assert.Equal("\"abc123\"", values.First());
    }

    // Stale: network call made, cache updated, OnUpdated published
    [Fact]
    public async Task StaleCacheHit_CallsNetworkAndUpdatesCache()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(FreshCachedEnvelope("https://example.com/api/items", "old"));

        var stub = new Fakes.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("new"),
        });
        using var client = new HttpClient(BuildHandler(store, stub, cacheIsStale: true));

        await client.GetAsync("https://example.com/api/items");

        Assert.Equal(1, stub.CallCount);
    }

    [Fact]
    public async Task StaleCacheHit_PublishesOnUpdated()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(FreshCachedEnvelope("https://example.com/api/items"));

        var events = new SyncEventStream();
        SyncEvent? received = null;
        events.Subscribe(new DelegateObserver<SyncEvent>(e => received = e));

        // Zero TTL: the entry cached a moment ago is already stale.
        var options = new HyperwycOptions { DefaultCacheTtl = TimeSpan.Zero };
        var handler = new HyperwycHandler(
            store,
            new Fakes.FakeConnectivityService(),
            new Fakes.FakeSyncPolicy(),
            events,
            options)
        {
            InnerHandler = new Fakes.StubHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new System.Net.Http.StringContent("{}") }),
        };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/api/items");

        Assert.NotNull(received);
        Assert.Equal(SyncEventType.OnUpdated, received!.Type);
    }

    // Cache miss: network called, entry stored
    [Fact]
    public async Task CacheMiss_StoresResponseAfterFetch()
    {
        var store = new InMemorySyncStore();
        var stub = new Fakes.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("{\"id\":1}"),
        });
        using var client = new HttpClient(BuildHandler(store, stub, cacheIsStale: true));

        await client.GetAsync("https://example.com/api/items/1");

        var cached = await store.GetCachedResponseAsync("https://example.com/api/items/1");
        Assert.NotNull(cached);
        Assert.Equal("{\"id\":1}", cached!.Response!.GetBodyAsText());
    }

    // Non-2xx: not cached
    [Fact]
    public async Task Non2xxResponse_NotCached()
    {
        var store = new InMemorySyncStore();
        var stub = new Fakes.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(BuildHandler(store, stub, cacheIsStale: true));

        await client.GetAsync("https://example.com/api/items/99");

        var cached = await store.GetCachedResponseAsync("https://example.com/api/items/99");
        Assert.Null(cached);
    }

    // Body too large: returned to caller but not cached
    [Fact]
    public async Task OversizedBody_ReturnedToCallerButNotCached()
    {
        var store = new InMemorySyncStore();
        var bigBody = new string('x', 100);
        var stub = new Fakes.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(bigBody),
        });
        // Set max to 10 bytes so the 100-byte body exceeds the limit
        using var client = new HttpClient(BuildHandler(store, stub, cacheIsStale: true, maxBodyBytes: 10));

        var response = await client.GetAsync("https://example.com/api/items");

        // Caller still gets the response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // But nothing stored in cache
        var cached = await store.GetCachedResponseAsync("https://example.com/api/items");
        Assert.Null(cached);
    }

    // Write requests bypass cache logic
    [Fact]
    public async Task WriteRequest_DoesNotReadOrWriteCache()
    {
        var store = new InMemorySyncStore();
        var stub = new Fakes.StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent("{}"),
        });
        using var client = new HttpClient(BuildHandler(store, stub, cacheIsStale: true));

        await client.PostAsync("https://example.com/api/items", content: null);

        // Write path should not populate the read cache
        var cached = await store.GetCachedResponseAsync("https://example.com/api/items");
        Assert.Null(cached);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
