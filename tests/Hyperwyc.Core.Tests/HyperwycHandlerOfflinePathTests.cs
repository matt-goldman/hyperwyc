using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using System.Text;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

public class HyperwycHandlerOfflinePathTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HyperwycHandler BuildOfflineHandler(
        InMemoryStore store,
        HyperwycEventStream? events = null,
        HyperwycOptions? options = null)
    {
        var handler = new HyperwycHandler(
            store,
            new FakeConnectivityService(isConnected: false),
            events ?? new HyperwycEventStream(),
            options ?? new HyperwycOptions(), TestHealth())
        {
            // Inner handler should never be reached when offline.
            InnerHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
        };
        return handler;
    }

    private static Envelope SeedCachedEnvelope(string url, string body = "{}")
    {
        var envelope = new Envelope { Url = url, Method = "GET" };
        envelope.Response = new CachedResponse
        {
            StatusCode = 200,
            Body = System.Text.Encoding.UTF8.GetBytes(body),
            CachedAt = DateTimeOffset.UtcNow,
        };
        envelope.IsSynced = true; // cached responses are already synced; must not show in outbox
        return envelope;
    }

    // -------------------------------------------------------------------------
    // Offline write path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OfflineWrite_AddsEnvelopeToOutbox()
    {
        var store = new InMemoryStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        await client.PostAsync("https://example.com/api/orders",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Single(outbox);
        Assert.Equal("https://example.com/api/orders", outbox[0].Url);
        Assert.Equal("POST", outbox[0].Method);
    }

    [Fact]
    public async Task OfflineWrite_EnvelopeIsNotSynced()
    {
        var store = new InMemoryStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        await client.PostAsync("https://example.com/api/orders", content: null);

        var outbox = await store.GetPendingOutboxAsync();
        Assert.False(outbox[0].IsSynced);
    }

    [Fact]
    public async Task OfflineWrite_PublishesOnQueuedEvent()
    {
        var store = new InMemoryStore();
        var events = new HyperwycEventStream();
        HyperwycEvent? received = null;
        events.Subscribe(new DelegateObserver<HyperwycEvent>(e => received = e));

        using var client = new HttpClient(BuildOfflineHandler(store, events));
        await client.PostAsync("https://example.com/api/orders", content: null);

        Assert.NotNull(received);
        Assert.Equal(HyperwycEventType.OnQueued, received!.Type);
        Assert.Equal("https://example.com/api/orders", received.Url);
    }

    [Fact]
    public async Task OfflineWrite_ReturnsSyntheticQueuedResponse()
    {
        var store = new InMemoryStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        var response = await client.PostAsync("https://example.com/api/orders", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(HyperwycResponseFactory.StatusHeader, out var values));
        Assert.Equal("Queued", values!.First());
    }


    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task OfflineWrite_AllWriteMethods_AreQueued(string method)
    {
        var store = new InMemoryStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method),
            "https://example.com/api/resource"));

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Single(outbox);
    }

    // -------------------------------------------------------------------------
    // Offline read path — cache available
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OfflineRead_CacheAvailable_ReturnsCachedResponse()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/items", "[1,2,3]"));

        using var client = new HttpClient(BuildOfflineHandler(store));
        var response = await client.GetAsync("https://example.com/api/items");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[1,2,3]", body);
    }

    [Fact]
    public async Task OfflineRead_CacheAvailable_DoesNotAddToOutbox()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/items"));

        using var client = new HttpClient(BuildOfflineHandler(store));
        await client.GetAsync("https://example.com/api/items");

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task OfflineRead_ServesCachedResponseWithinItsTtl()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/items", "cached-data"));

        var handler = new HyperwycHandler(
            store,
            new FakeConnectivityService(isConnected: false),
            new HyperwycEventStream(),
            TestOptions.WithTtl(TimeSpan.FromMinutes(5)), TestHealth())
        { InnerHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)) };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.com/api/items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cached-data", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OfflineRead_PastItsTtl_ServesNothingRatherThanSomethingTooOld()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/items", "too-old"));

        // The TTL means the same thing offline as online: how old a stored response may be and
        // still be served. Past it the caller gets the offline response as though nothing were
        // cached — an application can act on "no data" and cannot detect "quietly too old".
        var handler = new HyperwycHandler(
            store,
            new FakeConnectivityService(isConnected: false),
            new HyperwycEventStream(),
            TestOptions.WithTtl(TimeSpan.Zero), TestHealth())
        { InnerHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)) };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.com/api/items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
        Assert.DoesNotContain("too-old", await response.Content.ReadAsStringAsync());
    }

    // -------------------------------------------------------------------------
    // Offline read path — no cache
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OfflineRead_NoCacheAvailable_ReturnsSyntheticOfflineResponse()
    {
        var store = new InMemoryStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        var response = await client.GetAsync("https://example.com/api/items");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(HyperwycResponseFactory.StatusHeader, out var values));
        Assert.Equal("Offline", values!.First());
    }


    [Fact]
    public async Task OfflineRead_NoCacheAvailable_DoesNotAddToOutbox()
    {
        var store = new InMemoryStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        await client.GetAsync("https://example.com/api/items");

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Empty(outbox);
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
