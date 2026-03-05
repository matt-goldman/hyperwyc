using System.Net;
using System.Text;
using Restyc.Models;
using Restyc.Tests.Fakes;
using Xunit;

namespace Restyc.Tests;

public class RestycHandlerOfflinePathTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RestycHandler BuildOfflineHandler(
        InMemorySyncStore store,
        SyncEventStream? events = null)
    {
        var handler = new RestycHandler(
            store,
            new FakeConnectivityService(isConnected: false),
            new FakeSyncPolicy(),
            new FakeStalenessEvaluator(),
            events ?? new SyncEventStream(),
            new RestycOptions())
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
            Body = body,
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
        var store = new InMemorySyncStore();
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
        var store = new InMemorySyncStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        await client.PostAsync("https://example.com/api/orders", content: null);

        var outbox = await store.GetPendingOutboxAsync();
        Assert.False(outbox[0].IsSynced);
    }

    [Fact]
    public async Task OfflineWrite_PublishesOnQueuedEvent()
    {
        var store = new InMemorySyncStore();
        var events = new SyncEventStream();
        SyncEvent? received = null;
        events.Subscribe(new DelegateObserver<SyncEvent>(e => received = e));

        using var client = new HttpClient(BuildOfflineHandler(store, events));
        await client.PostAsync("https://example.com/api/orders", content: null);

        Assert.NotNull(received);
        Assert.Equal(SyncEventType.OnQueued, received!.Type);
        Assert.Equal("https://example.com/api/orders", received.Url);
    }

    [Fact]
    public async Task OfflineWrite_ReturnsSyntheticQueuedResponse()
    {
        var store = new InMemorySyncStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        var response = await client.PostAsync("https://example.com/api/orders", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(RestycResponseFactory.StatusHeader, out var values));
        Assert.Equal("Queued", values!.First());
    }

    [Fact]
    public async Task OfflineWrite_DoesNotCallInnerHandler()
    {
        var store = new InMemorySyncStore();
        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new RestycHandler(
            store,
            new FakeConnectivityService(isConnected: false),
            new FakeSyncPolicy(),
            new FakeStalenessEvaluator(),
            new SyncEventStream(),
            new RestycOptions())
        { InnerHandler = stub };
        using var client = new HttpClient(handler);

        await client.PostAsync("https://example.com/api/orders", content: null);

        Assert.Equal(0, stub.CallCount);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task OfflineWrite_AllWriteMethods_AreQueued(string method)
    {
        var store = new InMemorySyncStore();
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
        var store = new InMemorySyncStore();
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
        var store = new InMemorySyncStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/items"));

        using var client = new HttpClient(BuildOfflineHandler(store));
        await client.GetAsync("https://example.com/api/items");

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task OfflineRead_ServesStaleCacheWhenOffline()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(SeedCachedEnvelope("https://example.com/api/items", "stale-data"));

        // Even a strictly-stale evaluator should not prevent cache serving when offline.
        var handler = new RestycHandler(
            store,
            new FakeConnectivityService(isConnected: false),
            new FakeSyncPolicy(),
            new FakeStalenessEvaluator(isStale: true),   // would be stale online
            new SyncEventStream(),
            new RestycOptions())
        { InnerHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)) };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync("https://example.com/api/items");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stale-data", body);
    }

    // -------------------------------------------------------------------------
    // Offline read path — no cache
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OfflineRead_NoCacheAvailable_ReturnsSyntheticOfflineResponse()
    {
        var store = new InMemorySyncStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        var response = await client.GetAsync("https://example.com/api/items");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.TryGetValues(RestycResponseFactory.StatusHeader, out var values));
        Assert.Equal("Offline", values!.First());
    }

    [Fact]
    public async Task OfflineRead_NoCacheAvailable_DoesNotAddToOutbox()
    {
        var store = new InMemorySyncStore();
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
