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
    public async Task NetworkFirst_NetworkThrowsAndNoCache_AnswersOffline()
    {
        var store = new InMemoryStore();
        var stub = NetworkFailing();
        using var client = new HttpClient(BuildHandler(store, stub, SourcePriority.NetworkFirst));

        // No answer from the transport is the offline case, however the connectivity service
        // described it — so the caller gets "no data", not an exception.
        var response = await client.GetAsync(Url);

        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
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
    public async Task CacheFirst_NetworkThrows_DoesNotServeTheStaleCache()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedEnvelope());
        var stub = NetworkFailing();
        using var client = new HttpClient(
            BuildHandler(store, stub, SourcePriority.CacheFirst, cacheIsStale: true));

        // The TTL is a validity bound, so a stale copy is not served here either — CacheFirst
        // already had its chance and judged it stale. What the caller gets is the offline
        // answer rather than an exception: the failure is the absence of data, not a fault.
        var response = await client.GetAsync(Url);

        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
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
