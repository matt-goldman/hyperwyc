using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers <see cref="SourcePriority"/> resolution on the read paths. Until issue #27
/// these presets were public no-ops: the handler applied cache-first semantics
/// regardless of the route policy's <see cref="Models.RoutePolicy.SourcePriority"/>.
/// </summary>
public class SourcePriorityTests
{
    private const string Url = "https://example.com/api/items";

    private static HyperwycHandler BuildHandler(
        InMemoryStore store,
        StubHttpMessageHandler inner,
        SourcePriority strategy,
        bool isConnected = true,
        bool cacheIsStale = false) =>
        new(
            store,
            new FakeConnectivityService(isConnected),
            new HyperwycEventStream(),
            // A zero TTL makes every cached entry stale; CachedEnvelope stamps CachedAt as now.
            OptionsFor(strategy, cacheIsStale), TestHealth())
        { InnerHandler = inner };

    private static Envelope CachedEnvelope(string body = "cached")
    {
        var envelope = new Envelope { Url = Url, Method = "GET" };
        envelope.Response = new CachedResponse
        {
            StatusCode = 200,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
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
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, SourcePriority.NetworkOnly));

        var response = await client.GetAsync(Url);

        Assert.Equal(1, stub.CallCount);
        Assert.Equal("network", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NetworkOnly_DoesNotWriteToCache()
    {
        var store = new InMemoryStore();
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, SourcePriority.NetworkOnly));

        await client.GetAsync(Url);

        Assert.Null(await store.GetCachedResponseAsync(Url));
    }

    [Fact]
    public async Task NetworkOnly_Offline_DoesNotServeCache()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(
            BuildHandler(store, stub, SourcePriority.NetworkOnly, isConnected: false));

        var response = await client.GetAsync(Url);

        Assert.Equal(0, stub.CallCount);
        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
    }





    // -------------------------------------------------------------------------
    // NetworkFirst — network first, cache only as a failure fallback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task NetworkFirst_CallsNetwork_EvenWhenCacheIsFresh()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, SourcePriority.NetworkFirst));

        var response = await client.GetAsync(Url);

        Assert.Equal(1, stub.CallCount);
        Assert.Equal("network", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NetworkFirst_FallsBackToCache_WhenNetworkThrows()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkFailing();
        using var client = new HttpClient(BuildHandler(store, stub, SourcePriority.NetworkFirst));

        var response = await client.GetAsync(Url);

        Assert.Equal("cached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NetworkFirst_NetworkThrowsAndNoCache_PropagatesException()
    {
        var store = new InMemoryStore();
        var stub = NetworkFailing();
        using var client = new HttpClient(BuildHandler(store, stub, SourcePriority.NetworkFirst));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Url));
    }

    [Fact]
    public async Task NetworkFirst_PopulatesCache_SoTheFallbackHasSomethingToServe()
    {
        var store = new InMemoryStore();
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, SourcePriority.NetworkFirst));

        await client.GetAsync(Url);

        var cached = await store.GetCachedResponseAsync(Url);
        Assert.NotNull(cached);
        Assert.Equal("network", cached!.Response!.GetBodyAsText());
    }

    // -------------------------------------------------------------------------
    // CacheFirst — the default, unchanged by issue #27
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CacheFirst_ServesFreshCache_WithoutCallingNetwork()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(BuildHandler(store, stub, SourcePriority.CacheFirst));

        var response = await client.GetAsync(Url);

        Assert.Equal(0, stub.CallCount);
        Assert.Equal("cached", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CacheFirst_StaleCache_CallsNetwork()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkReturning();
        using var client = new HttpClient(
            BuildHandler(store, stub, SourcePriority.CacheFirst, cacheIsStale: true));

        var response = await client.GetAsync(Url);

        Assert.Equal(1, stub.CallCount);
        Assert.Equal("network", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CacheFirst_NetworkThrows_DoesNotFallBackToCache()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkFailing();
        using var client = new HttpClient(
            BuildHandler(store, stub, SourcePriority.CacheFirst, cacheIsStale: true));

        // Fallback-on-failure is NetworkFirst's contract, not CacheFirst's. CacheFirst
        // already had its chance to serve the cache and judged it stale.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Url));
    }

    /// <summary>
    /// Strategy and TTL now both come from the resolved <see cref="RoutePolicy"/>, so a test
    /// that wants a strategy sets the route map's default rather than passing a policy object.
    /// </summary>
    private static HyperwycOptions OptionsFor(SourcePriority strategy, bool cacheIsStale)
    {
        var options = new HyperwycOptions();
        options.Routes.Default = new RoutePolicy
        {
            SourcePriority = strategy,
            Ttl = cacheIsStale ? TimeSpan.Zero : TimeSpan.FromMinutes(5),
        };
        return options;
    }
}
