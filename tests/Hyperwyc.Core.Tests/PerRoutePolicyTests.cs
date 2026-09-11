using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using System.Text;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue #22 end to end: a matched <see cref="RoutePolicy"/> changes what the handler
/// and the orchestrator actually do, not merely what a map returns.
/// </summary>
public class PerRoutePolicyTests
{
    private const string Products = "https://example.com/api/products";
    private const string Payments = "https://example.com/api/payments/charge";

    private static HyperwycHandler Handler(
        InMemoryStore store, HttpMessageHandler inner, HyperwycOptions options, bool connected) =>
        new(store, new FakeConnectivityService(connected), new HyperwycEventStream(), options, TestHealth())
        { InnerHandler = inner };

    private static CachedResponse Cached(string url, string body) =>
        new()
        {
            Url = url,
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(body),
            CachedAt = DateTimeOffset.UtcNow,
        };

    // -------------------------------------------------------------------------
    // Strategy varies per route
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OneRouteServesFromCacheWhileAnotherAlwaysHitsTheNetwork()
    {
        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached(Products, "cached-products"));
        await store.PutCachedResponseAsync(Cached(Payments, "cached-payments"));

        var options = new HyperwycOptions();
        options.Routes
            .For("/api/*", RoutePolicy.CacheFirst(TimeSpan.FromHours(1)))
            .For("/api/payments/*", RoutePolicy.NetworkOnly());

        var network = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("from-network") });
        using var client = new HttpClient(Handler(store, network, options, connected: true));

        Assert.Equal("cached-products", await (await client.GetAsync(Products)).Content.ReadAsStringAsync());
        Assert.Equal("from-network", await (await client.GetAsync(Payments)).Content.ReadAsStringAsync());
    }

    // -------------------------------------------------------------------------
    // TTL varies per route
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ARouteWithAShortTtlRefetchesWhileALongOneDoesNot()
    {
        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached(Products, "stale-products"));
        await store.PutCachedResponseAsync(Cached("https://example.com/api/reference/codes", "reference"));

        var options = new HyperwycOptions();
        options.Routes
            .For("/api/products/*", RoutePolicy.CacheFirst(TimeSpan.Zero))          // always stale
            .For("/api/reference/*", RoutePolicy.CacheFirst(TimeSpan.FromDays(7))); // always fresh

        var network = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fresh") });
        using var client = new HttpClient(Handler(store, network, options, connected: true));

        Assert.Equal("fresh", await (await client.GetAsync(Products)).Content.ReadAsStringAsync());
        Assert.Equal("reference",
            await (await client.GetAsync("https://example.com/api/reference/codes")).Content.ReadAsStringAsync());
    }

    // -------------------------------------------------------------------------
    // NetworkOnly governs writes as well as reads
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OfflineWriteToANetworkOnlyRoute_IsNotQueued()
    {
        // The payments case: deferring the write is the wrong answer, so Hyperwyc declines
        // custody rather than issuing a 202 it may honour hours later.
        var store = new InMemoryStore();
        var options = new HyperwycOptions();
        options.Routes.For("/api/payments/*", RoutePolicy.NetworkOnly());

        var transport = new StubHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("offline")));
        using var client = new HttpClient(Handler(store, transport, options, connected: false));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync(Payments, new StringContent("{}")));

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task OfflineWriteToAnyOtherRoute_IsStillQueued()
    {
        var store = new InMemoryStore();
        var options = new HyperwycOptions();
        options.Routes.For("/api/payments/*", RoutePolicy.NetworkOnly());

        using var client = new HttpClient(Handler(
            store,
            new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            options,
            connected: false));

        var response = await client.PostAsync("https://example.com/api/sales", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(await store.GetPendingOutboxAsync());
    }

    // -------------------------------------------------------------------------
    // Invalidation varies per route, on both the direct and replay paths
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ARouteCanOptOutOfInvalidationOnWrite()
    {
        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached("https://example.com/api/log", "kept"));

        var options = new HyperwycOptions();
        options.Routes.For("/api/log/*", RoutePolicy.CacheFirst() with { InvalidateCacheOnWrite = false });

        using var client = new HttpClient(Handler(
            store, new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            options, connected: true));

        await client.PostAsync("https://example.com/api/log", new StringContent("{}"));

        Assert.NotNull(await store.GetCachedResponseAsync("https://example.com/api/log"));
    }

    [Fact]
    public async Task InvalidationOnTheReplayPath_UsesTheRoutePolicyToo()
    {
        // The orchestrator resolves through the same map, so a replayed write honours the
        // route's decision rather than a global one.
        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached("https://example.com/api/log", "kept"));
        await store.UpsertQueuedWriteAsync(new QueuedWrite { Url = "https://example.com/api/log", Method = "POST" });

        var options = new HyperwycOptions();
        options.Routes.For("/api/log/*", RoutePolicy.CacheFirst() with { InvalidateCacheOnWrite = false });

        await using var orchestrator = new OutboxProcessor(
            store,
            new FakeConnectivityService(isConnected: true),
            new HyperwycEventStream(),
            options,
            new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)), TestHealth());

        await orchestrator.FlushAsync();

        Assert.NotNull(await store.GetCachedResponseAsync("https://example.com/api/log"));
    }

    // -------------------------------------------------------------------------
    // The body cap varies per route (issue #20)
    // -------------------------------------------------------------------------

    private static StubHttpMessageHandler BodyOf(int bytes) =>
        new(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', bytes)),
        });

    [Fact]
    public async Task ARouteCapRaisesTheGlobalOneForThatRouteAlone()
    {
        // The point of the override: one endpoint returns a document, and the rest of the
        // application should not gain the headroom it needed.
        var store = new InMemoryStore();

        var options = new HyperwycOptions { MaxCachedResponseBodyBytes = 10 };
        options.Routes.For("/api/reports/*", RoutePolicy.CacheFirst() with { MaxCachedResponseBodyBytes = 1000 });

        using var client = new HttpClient(Handler(store, BodyOf(100), options, connected: true));

        await client.GetAsync("https://example.com/api/reports/annual");
        await client.GetAsync("https://example.com/api/products");

        Assert.NotNull(await store.GetCachedResponseAsync("https://example.com/api/reports/annual"));
        Assert.Null(await store.GetCachedResponseAsync("https://example.com/api/products"));
    }

    [Fact]
    public async Task ARouteCapLowersTheGlobalOneToo()
    {
        var store = new InMemoryStore();

        var options = new HyperwycOptions { MaxCachedResponseBodyBytes = 1000 };
        options.Routes.For("/api/feed/*", RoutePolicy.CacheFirst() with { MaxCachedResponseBodyBytes = 10 });

        using var client = new HttpClient(Handler(store, BodyOf(100), options, connected: true));

        await client.GetAsync("https://example.com/api/feed/latest");

        Assert.Null(await store.GetCachedResponseAsync("https://example.com/api/feed/latest"));
    }

    [Fact]
    public async Task ARouteWithNoCapOfItsOwnUsesTheGlobalOne()
    {
        // Unset means inherit, which is what keeps 512 KB from having to be restated on every
        // policy that does not care about it.
        var store = new InMemoryStore();

        var options = new HyperwycOptions { MaxCachedResponseBodyBytes = 1000 };
        options.Routes.For("/api/*", RoutePolicy.CacheFirst(TimeSpan.FromHours(1)));

        using var client = new HttpClient(Handler(store, BodyOf(100), options, connected: true));

        await client.GetAsync("https://example.com/api/products");

        Assert.NotNull(await store.GetCachedResponseAsync("https://example.com/api/products"));
    }

    [Fact]
    public async Task ARouteCapOfZeroCachesNoBody()
    {
        // Zero is the supported way to say "store nothing here" on a route that should still be
        // fetched and invalidated normally. NetworkOnly is the blunter instrument.
        var store = new InMemoryStore();

        var options = new HyperwycOptions();
        options.Routes.For("/api/feed/*", RoutePolicy.CacheFirst() with { MaxCachedResponseBodyBytes = 0 });

        using var client = new HttpClient(Handler(store, BodyOf(100), options, connected: true));

        await client.GetAsync("https://example.com/api/feed/latest");

        Assert.Null(await store.GetCachedResponseAsync("https://example.com/api/feed/latest"));
    }
}
