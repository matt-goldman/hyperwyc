using System.Net;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

public class HyperwycHandlerOnlinePathTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HyperwycHandler BuildHandler(
        InMemorySyncStore store,
        StubHttpMessageHandler inner,
        bool shouldInvalidate = true,
        bool cacheIsStale = true)
    {
        var handler = new HyperwycHandler(
            store,
            new FakeConnectivityService(isConnected: true),
            new SyncEventStream(),
            Options(shouldInvalidate, cacheIsStale))
        {
            InnerHandler = inner,
        };
        return handler;
    }

    private static HttpClient MakeClient(HyperwycHandler handler) => new(handler);

    private static Envelope SeedCachedEnvelope(
        string url,
        string body = "{}",
        int statusCode = 200)
    {
        var envelope = new Envelope { Url = url, Method = "GET" };
        envelope.Response = new CachedResponse
        {
            StatusCode = statusCode,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            CachedAt = DateTimeOffset.UtcNow,
        };
        envelope.IsSynced = true; // cached responses are already synced
        return envelope;
    }

    // -------------------------------------------------------------------------
    // Write path — 2xx
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnlineWrite_2xx_InvalidatesCacheForUrl()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/orders"));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = MakeClient(BuildHandler(store, stub, shouldInvalidate: true));

        await client.PostAsync("https://example.com/api/orders", content: null);

        var result = await store.GetCachedResponseAsync("https://example.com/api/orders");
        Assert.Null(result); // response field cleared
    }

    [Fact]
    public async Task OnlineWrite_2xx_PublishesOnSyncedEvent()
    {
        var store = new InMemorySyncStore();
        var events = new SyncEventStream();
        SyncEvent? received = null;
        events.Subscribe(new DelegateObserver<SyncEvent>(e => received = e));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Created));
        var handler = new HyperwycHandler(
            store,
            new FakeConnectivityService(),
            events,
            new HyperwycOptions())
        { InnerHandler = stub };
        using var client = new HttpClient(handler);

        await client.PostAsync("https://example.com/api/orders", content: null);

        Assert.NotNull(received);
        Assert.Equal(SyncEventType.OnSynced, received!.Type);
    }

    [Fact]
    public async Task OnlineWrite_2xx_WithInvalidationDisabled_DoesNotClearCache()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/orders"));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = MakeClient(BuildHandler(store, stub, shouldInvalidate: false));

        await client.PostAsync("https://example.com/api/orders", content: null);

        var result = await store.GetCachedResponseAsync("https://example.com/api/orders");
        Assert.NotNull(result); // cache preserved
    }

    [Fact]
    public async Task OnlineWrite_2xx_ReturnsResponseToCallerWithCorrectStatusCode()
    {
        var store = new InMemorySyncStore();
        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Created));
        using var client = MakeClient(BuildHandler(store, stub));

        var response = await client.PostAsync("https://example.com/api/orders", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task OnlineWrite_2xx_NumericId_InvalidatesCollectionAndResourceCache()
    {
        var store = new InMemorySyncStore();
        // Both the collection and the specific resource are cached.
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/orders"));
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/orders/42"));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = MakeClient(BuildHandler(store, stub, shouldInvalidate: true));

        await client.PutAsync("https://example.com/api/orders/42", content: null);

        // Both collection and resource entries should be invalidated via the
        // derived prefix "https://example.com/api/orders".
        Assert.Null(await store.GetCachedResponseAsync("https://example.com/api/orders"));
        Assert.Null(await store.GetCachedResponseAsync("https://example.com/api/orders/42"));
    }

    [Fact]
    public async Task OnlineWrite_2xx_GuidId_InvalidatesCollectionCache()
    {
        var store = new InMemorySyncStore();
        var id = "550e8400-e29b-41d4-a716-446655440000";
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/notes"));
        await store.UpsertAsync(SeedCachedEnvelope($"https://example.com/api/notes/{id}"));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = MakeClient(BuildHandler(store, stub, shouldInvalidate: true));

        await client.DeleteAsync($"https://example.com/api/notes/{id}");

        Assert.Null(await store.GetCachedResponseAsync("https://example.com/api/notes"));
        Assert.Null(await store.GetCachedResponseAsync($"https://example.com/api/notes/{id}"));
    }

    [Fact]
    public async Task DeriveInvalidationPrefix_NonIdSegment_ReturnsFullPath()
    {
        var uri = new Uri("https://example.com/api/orders");
        var prefix = HyperwycHandler.DeriveInvalidationPrefix(uri);
        Assert.Equal("https://example.com/api/orders", prefix);
    }

    [Fact]
    public async Task DeriveInvalidationPrefix_NumericSegment_ReturnsParentPath()
    {
        var uri = new Uri("https://example.com/api/orders/42");
        var prefix = HyperwycHandler.DeriveInvalidationPrefix(uri);
        Assert.Equal("https://example.com/api/orders", prefix);
    }

    [Fact]
    public async Task DeriveInvalidationPrefix_GuidSegment_ReturnsParentPath()
    {
        var uri = new Uri("https://example.com/api/notes/550e8400-e29b-41d4-a716-446655440000");
        var prefix = HyperwycHandler.DeriveInvalidationPrefix(uri);
        Assert.Equal("https://example.com/api/notes", prefix);
    }

    // -------------------------------------------------------------------------
    // Write path — non-2xx
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnlineWrite_Non2xx_DoesNotInvalidateCache()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/orders"));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = MakeClient(BuildHandler(store, stub, shouldInvalidate: true));

        await client.PostAsync("https://example.com/api/orders", content: null);

        var result = await store.GetCachedResponseAsync("https://example.com/api/orders");
        Assert.NotNull(result); // cache untouched
    }

    [Fact]
    public async Task OnlineWrite_Non2xx_DoesNotPublishOnSyncedEvent()
    {
        var store = new InMemorySyncStore();
        var events = new SyncEventStream();
        var received = new List<SyncEvent>();
        events.Subscribe(new DelegateObserver<SyncEvent>(received.Add));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var handler = new HyperwycHandler(
            store,
            new FakeConnectivityService(),
            events,
            new HyperwycOptions())
        { InnerHandler = stub };
        using var client = new HttpClient(handler);

        await client.PostAsync("https://example.com/api/orders", content: null);

        Assert.Empty(received);
    }

    [Fact]
    public async Task OnlineWrite_Non2xx_ReturnsErrorResponseToCaller()
    {
        var store = new InMemorySyncStore();
        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = MakeClient(BuildHandler(store, stub));

        var response = await client.PostAsync("https://example.com/api/orders", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Read path — fresh cache
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnlineRead_FreshCache_DoesNotCallNetwork()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/items", body: "[1,2,3]"));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var client = MakeClient(BuildHandler(store, stub, cacheIsStale: false));

        await client.GetAsync("https://example.com/api/items");

        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task OnlineRead_FreshCache_ReturnsCachedStatusAndBody()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/items", body: "[1,2,3]", statusCode: 200));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = MakeClient(BuildHandler(store, stub, cacheIsStale: false));

        var response = await client.GetAsync("https://example.com/api/items");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[1,2,3]", body);
    }

    // -------------------------------------------------------------------------
    // Read path — stale / missing cache
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnlineRead_StaleCache_CallsNetworkAndReturnsFreshResponse()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/items", body: "[\"stale\"]"));

        var freshResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[\"fresh\"]"),
        };
        var stub = new StubHttpMessageHandler(freshResponse);
        using var client = MakeClient(BuildHandler(store, stub, cacheIsStale: true));

        var response = await client.GetAsync("https://example.com/api/items");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(1, stub.CallCount);          // network was hit
        Assert.Equal("[\"fresh\"]", body);        // caller receives fresh body, not stale
    }

    [Fact]
    public async Task OnlineRead_NoCache_CallsNetworkAndStoresResponse()
    {
        var store = new InMemorySyncStore();
        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":1}"),
        });
        using var client = MakeClient(BuildHandler(store, stub));

        await client.GetAsync("https://example.com/api/items/1");

        Assert.Equal(1, stub.CallCount);
        var cached = await store.GetCachedResponseAsync("https://example.com/api/items/1");
        Assert.NotNull(cached);
    }

    [Fact]
    public async Task OnlineRead_NetworkSuccess_PublishesOnUpdatedEvent()
    {
        var store = new InMemorySyncStore();
        var events = new SyncEventStream();
        SyncEvent? received = null;
        events.Subscribe(new DelegateObserver<SyncEvent>(e => received = e));

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        });
        var handler = new HyperwycHandler(
            store,
            new FakeConnectivityService(),
            events,
            new HyperwycOptions())
        { InnerHandler = stub };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/api/items");

        Assert.NotNull(received);
        Assert.Equal(SyncEventType.OnUpdated, received!.Type);
    }

    [Fact]
    public async Task OnlineRead_Non2xxNetwork_DoesNotStoreResponse()
    {
        var store = new InMemorySyncStore();
        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = MakeClient(BuildHandler(store, stub));

        await client.GetAsync("https://example.com/api/items/99");

        var cached = await store.GetCachedResponseAsync("https://example.com/api/items/99");
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

    /// <summary>
    /// Strategy, TTL and invalidate-on-write all come from the resolved route policy now.
    /// A zero TTL makes every cached entry stale.
    /// </summary>
    private static HyperwycOptions Options(bool shouldInvalidate, bool cacheIsStale)
    {
        var options = new HyperwycOptions();
        options.Routes.Default = new RoutePolicy
        {
            Ttl = cacheIsStale ? TimeSpan.Zero : TimeSpan.FromMinutes(5),
            InvalidateCacheOnWrite = shouldInvalidate,
        };
        return options;
    }
}
