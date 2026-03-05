using System.Net;
using System.Text;
using Restyc.Models;
using Restyc.Tests.Fakes;
using Xunit;

namespace Restyc.Tests;

public class IdempotencyKeyTests
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (RestycHandler handler, StubHttpMessageHandler stub) BuildOnlineHandler(
        InMemorySyncStore? store = null)
    {
        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new RestycHandler(
            store ?? new InMemorySyncStore(),
            new FakeConnectivityService(isConnected: true),
            new FakeSyncPolicy(),
            new FakeStalenessEvaluator(),
            new SyncEventStream(),
            new RestycOptions())
        { InnerHandler = stub };
        return (handler, stub);
    }

    private static RestycHandler BuildOfflineHandler(InMemorySyncStore store)
    {
        return new RestycHandler(
            store,
            new FakeConnectivityService(isConnected: false),
            new FakeSyncPolicy(),
            new FakeStalenessEvaluator(),
            new SyncEventStream(),
            new RestycOptions())
        { InnerHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)) };
    }

    // -------------------------------------------------------------------------
    // Online write — header absent → injected
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnlineWrite_NoHeader_InjectsIdempotencyKey()
    {
        var (handler, stub) = BuildOnlineHandler();
        using var client = new HttpClient(handler);

        await client.PostAsync("https://example.com/api/orders", content: null);

        var sentRequest = stub.LastRequest!;
        Assert.True(sentRequest.Headers.Contains(IdempotencyKeyHeader));
    }

    [Fact]
    public async Task OnlineWrite_NoHeader_InjectedValueIsValidGuid()
    {
        var (handler, stub) = BuildOnlineHandler();
        using var client = new HttpClient(handler);

        await client.PostAsync("https://example.com/api/orders", content: null);

        var value = stub.LastRequest!.Headers.GetValues(IdempotencyKeyHeader).First();
        Assert.True(Guid.TryParse(value, out _));
    }

    [Fact]
    public async Task OnlineWrite_NoHeader_EachRequestGetsDifferentKey()
    {
        var (handler, stub) = BuildOnlineHandler();
        using var client = new HttpClient(handler);

        await client.PostAsync("https://example.com/api/orders", content: null);
        var key1 = stub.LastRequest!.Headers.GetValues(IdempotencyKeyHeader).First();

        await client.PostAsync("https://example.com/api/orders", content: null);
        var key2 = stub.LastRequest!.Headers.GetValues(IdempotencyKeyHeader).First();

        Assert.NotEqual(key1, key2);
    }

    // -------------------------------------------------------------------------
    // Online write — header present → preserved
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnlineWrite_HeaderPresent_IsNotOverwritten()
    {
        var (handler, stub) = BuildOnlineHandler();
        using var client = new HttpClient(handler);
        var knownKey = Guid.NewGuid().ToString();

        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/orders");
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, knownKey);
        await client.SendAsync(request);

        var sentValue = stub.LastRequest!.Headers.GetValues(IdempotencyKeyHeader).First();
        Assert.Equal(knownKey, sentValue);
    }

    // -------------------------------------------------------------------------
    // Read requests — no header injected
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task ReadRequest_NoIdempotencyKeyInjected(string method)
    {
        var (handler, stub) = BuildOnlineHandler();
        using var client = new HttpClient(handler);

        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method),
            "https://example.com/api/items"));

        Assert.False(stub.LastRequest!.Headers.Contains(IdempotencyKeyHeader));
    }

    // -------------------------------------------------------------------------
    // Offline write — envelope.Id matches the injected key
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OfflineWrite_NoHeader_EnvelopeIdMatchesInjectedKey()
    {
        var store = new InMemorySyncStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        await client.PostAsync("https://example.com/api/orders", content: null);

        var outbox = await store.GetPendingOutboxAsync();
        var envelope = outbox[0];
        // The key stored in RequestHeaders must equal envelope.Id
        Assert.True(envelope.RequestHeaders.TryGetValue(IdempotencyKeyHeader, out var storedKey));
        Assert.Equal(envelope.Id, storedKey);
    }

    [Fact]
    public async Task OfflineWrite_HeaderPresent_EnvelopeIdEqualsCallerKey()
    {
        var store = new InMemorySyncStore();
        var knownKey = Guid.NewGuid().ToString();

        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/orders")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, knownKey);

        using var client = new HttpClient(BuildOfflineHandler(store));
        await client.SendAsync(request);

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Equal(knownKey, outbox[0].Id);
    }

    // -------------------------------------------------------------------------
    // Replay simulation — same key re-injected produces same envelope Id
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OfflineWrite_KnownKey_ThenSendAgainWithSameKey_SameEnvelopeId()
    {
        var store = new InMemorySyncStore();
        var knownKey = Guid.NewGuid().ToString();

        async Task SendWithKey()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/orders");
            req.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, knownKey);
            using var client = new HttpClient(BuildOfflineHandler(store));
            await client.SendAsync(req);
        }

        await SendWithKey();
        var firstId = (await store.GetPendingOutboxAsync())[0].Id;

        await store.ResetAsync();

        await SendWithKey();
        var secondId = (await store.GetPendingOutboxAsync())[0].Id;

        Assert.Equal(knownKey, firstId);
        Assert.Equal(knownKey, secondId);
    }
}
