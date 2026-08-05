using System.Net;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers <see cref="CacheStrategy"/> resolution on the read paths. Until issue #27
/// these presets were public no-ops: the handler applied cache-first semantics
/// regardless of what <see cref="Interfaces.ISyncPolicy.GetStrategy"/> returned.
/// </summary>
public class CacheStrategyTests
{
    private const string Url = "https://example.com/api/items";

    private static HyperwycHandler BuildHandler(
        InMemorySyncStore store,
        StubHttpMessageHandler inner,
        CacheStrategy strategy,
        bool isConnected = true,
        bool cacheIsStale = false) =>
        new(
            store,
            new FakeConnectivityService(isConnected),
            new FakeSyncPolicy(strategy),
            new FakeStalenessEvaluator(cacheIsStale),
            new SyncEventStream(),
            new HyperwycOptions())
        { InnerHandler = inner };

    private static Envelope CachedEnvelope(string body = "cached")
    {
        var envelope = new Envelope { Url = Url, Method = "GET" };
        envelope.Response = new CachedResponse
        {
            StatusCode = 200,
            Body = body,
            CachedAt = DateTimeOffset.UtcNow,
        };
        envelope.IsSynced = true;
        return envelope;
    }

    private static StubHttpMessageHandler NetworkReturning(string body = "network") =>
        new(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    private static StubHttpMessageHandler NetworkFailing() =>
        new((Func<HttpRequestMessage, Task<HttpResponseMessage>>)(
            _ => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("no route to host"))));

    // -------------------------------------------------------------------------
    // NetworkOnly — never reads or writes the cache
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NetworkOnly_IgnoresFreshCache_AndCallsNetwork()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, CacheStrategy.NetworkOnly));

        var response = await client.GetAsync(Url);

        Assert.Equal(1, stub.CallCount);
        Assert.Equal("network", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NetworkOnly_DoesNotWriteToCache()
    {
        var store = new InMemorySyncStore();
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, CacheStrategy.NetworkOnly));

        await client.GetAsync(Url);

        Assert.Null(await store.GetCachedResponseAsync(Url));
    }

    [Fact]
    public async Task NetworkOnly_Offline_DoesNotServeCache()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(
            BuildHandler(store, stub, CacheStrategy.NetworkOnly, isConnected: false));

        var response = await client.GetAsync(Url);

        Assert.Equal(0, stub.CallCount);
        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
    }

    // -------------------------------------------------------------------------
    // CacheOnly — never reaches the network
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CacheOnly_ServesCache_WithoutCallingNetwork()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, CacheStrategy.CacheOnly));

        var response = await client.GetAsync(Url);

        Assert.Equal(0, stub.CallCount);
        Assert.Equal("cached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CacheOnly_ServesStaleCache_RatherThanCallingNetwork()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(
            BuildHandler(store, stub, CacheStrategy.CacheOnly, cacheIsStale: true));

        var response = await client.GetAsync(Url);

        Assert.Equal(0, stub.CallCount);
        Assert.Equal("cached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CacheOnly_EmptyCache_ReturnsCacheMissWithoutCallingNetwork()
    {
        var store = new InMemorySyncStore();
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, CacheStrategy.CacheOnly));

        var response = await client.GetAsync(Url);

        Assert.Equal(0, stub.CallCount);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Not "Offline" — the device is online; the route opted out of the network.
        Assert.Equal("CacheMiss", response.Headers.GetValues("X-Hyperwyc-Status").Single());
    }

    [Fact]
    public async Task CacheOnly_EmptyCache_SignalPolicy_Returns503()
    {
        var store = new InMemorySyncStore();
        var stub = NetworkReturning();
        var handler = new HyperwycHandler(
            store,
            new FakeConnectivityService(isConnected: true),
            new FakeSyncPolicy(CacheStrategy.CacheOnly),
            new FakeStalenessEvaluator(isStale: false),
            new SyncEventStream(),
            new HyperwycOptions { OfflineResponsePolicy = OfflineResponsePolicy.Signal })
        { InnerHandler = stub };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // ApiFirst — network first, cache only as a failure fallback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ApiFirst_CallsNetwork_EvenWhenCacheIsFresh()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, CacheStrategy.ApiFirst));

        var response = await client.GetAsync(Url);

        Assert.Equal(1, stub.CallCount);
        Assert.Equal("network", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiFirst_FallsBackToCache_WhenNetworkThrows()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkFailing();
        using var client = new HttpClient(BuildHandler(store, stub, CacheStrategy.ApiFirst));

        var response = await client.GetAsync(Url);

        Assert.Equal("cached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiFirst_NetworkThrowsAndNoCache_PropagatesException()
    {
        var store = new InMemorySyncStore();
        var stub = NetworkFailing();
        using var client = new HttpClient(BuildHandler(store, stub, CacheStrategy.ApiFirst));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Url));
    }

    [Fact]
    public async Task ApiFirst_PopulatesCache_SoTheFallbackHasSomethingToServe()
    {
        var store = new InMemorySyncStore();
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, CacheStrategy.ApiFirst));

        await client.GetAsync(Url);

        var cached = await store.GetCachedResponseAsync(Url);
        Assert.NotNull(cached);
        Assert.Equal("network", cached!.Response!.Body);
    }

    // -------------------------------------------------------------------------
    // CacheFirst — the default, unchanged by issue #27
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CacheFirst_ServesFreshCache_WithoutCallingNetwork()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, CacheStrategy.CacheFirst));

        var response = await client.GetAsync(Url);

        Assert.Equal(0, stub.CallCount);
        Assert.Equal("cached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CacheFirst_StaleCache_CallsNetwork()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(
            BuildHandler(store, stub, CacheStrategy.CacheFirst, cacheIsStale: true));

        var response = await client.GetAsync(Url);

        Assert.Equal(1, stub.CallCount);
        Assert.Equal("network", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CacheFirst_NetworkThrows_DoesNotFallBackToCache()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkFailing();
        using var client = new HttpClient(
            BuildHandler(store, stub, CacheStrategy.CacheFirst, cacheIsStale: true));

        // Fallback-on-failure is ApiFirst's contract, not CacheFirst's. CacheFirst
        // already had its chance to serve the cache and judged it stale.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Url));
    }
}
