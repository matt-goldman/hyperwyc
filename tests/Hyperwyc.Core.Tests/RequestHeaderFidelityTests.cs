using System.Net;
using System.Text;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Hyperwyc sends the request the application made, and nothing else.
/// </summary>
/// <remarks>
/// Replaces <c>IdempotencyKeyTests</c>. Hyperwyc used to inject an
/// <c>Idempotency-Key</c> header on every mutating request, which only pays off if the
/// backend implements it — an assumption a transport-level library has no business
/// making (issue #39). Duplicate suppression is a concern between an application and
/// its API; these tests pin that Hyperwyc stays out of it while carrying faithfully
/// whatever the application does set.
/// </remarks>
public class RequestHeaderFidelityTests
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private static (HyperwycHandler handler, StubHttpMessageHandler stub) BuildOnlineHandler(
        InMemorySyncStore? store = null)
    {
        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new HyperwycHandler(
            store ?? new InMemorySyncStore(),
            new FakeConnectivityService(isConnected: true),
            new FakeSyncPolicy(),
            new FakeStalenessEvaluator(),
            new SyncEventStream(),
            new HyperwycOptions())
        { InnerHandler = stub };
        return (handler, stub);
    }

    private static HyperwycHandler BuildOfflineHandler(InMemorySyncStore store) =>
        new(
            store,
            new FakeConnectivityService(isConnected: false),
            new FakeSyncPolicy(),
            new FakeStalenessEvaluator(),
            new SyncEventStream(),
            new HyperwycOptions())
        { InnerHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)) };

    private static SyncOrchestrator BuildOrchestrator(
        InMemorySyncStore store, HttpMessageHandler transport) =>
        new(
            store,
            new FakeSyncPolicy(),
            new FakeConnectivityService(isConnected: true),
            new SyncEventStream(),
            new HyperwycOptions { ConnectivityDebounceDelay = TimeSpan.Zero },
            transport);

    // -------------------------------------------------------------------------
    // Nothing is added
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task OnlineWrite_AddsNoIdempotencyKey(string method)
    {
        var (handler, stub) = BuildOnlineHandler();
        using var client = new HttpClient(handler);

        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "https://example.com/api/items")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        Assert.False(stub.LastRequest!.Headers.Contains(IdempotencyKeyHeader));
    }

    [Fact]
    public async Task QueuedWrite_StoresNoIdempotencyKey()
    {
        var store = new InMemorySyncStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        await client.PostAsync("https://example.com/api/items", new StringContent("{}"));

        var envelope = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.False(envelope.RequestHeaders.ContainsKey(IdempotencyKeyHeader));
    }

    [Fact]
    public async Task Replay_AddsNoIdempotencyKey()
    {
        var store = new InMemorySyncStore();
        using (var client = new HttpClient(BuildOfflineHandler(store)))
            await client.PostAsync("https://example.com/api/items", new StringContent("{}"));

        HttpRequestMessage? sent = null;
        var transport = new StubHttpMessageHandler(req =>
        {
            sent = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        await using var orchestrator = BuildOrchestrator(store, transport);
        await orchestrator.FlushAsync();

        Assert.NotNull(sent);
        Assert.False(sent!.Headers.Contains(IdempotencyKeyHeader));
    }

    [Fact]
    public async Task EnvelopeId_IsNotDerivedFromAnyRequestHeader()
    {
        var store = new InMemorySyncStore();
        using var client = new HttpClient(BuildOfflineHandler(store));

        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/items")
        {
            Content = new StringContent("{}"),
        };
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, "caller-chosen-key");
        await client.SendAsync(request);

        // The envelope's identity is Hyperwyc's own bookkeeping and is never sent, so it
        // must not be quietly adopted from — or conflated with — an application header.
        var envelope = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.NotEqual("caller-chosen-key", envelope.Id);
        Assert.True(Guid.TryParse(envelope.Id, out _));
    }

    // -------------------------------------------------------------------------
    // What the application sets is carried faithfully
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CallerSuppliedIdempotencyKey_IsSentUnchanged()
    {
        var (handler, stub) = BuildOnlineHandler();
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/items")
        {
            Content = new StringContent("{}"),
        };
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, "order-42");
        await client.SendAsync(request);

        Assert.Equal("order-42", stub.LastRequest!.Headers.GetValues(IdempotencyKeyHeader).Single());
    }

    /// <summary>
    /// The mechanism that makes application-level idempotency work across replays: a key
    /// set at the call site is persisted with the envelope and replayed with the same
    /// value, however many attempts it takes.
    /// </summary>
    [Fact]
    public async Task CallerSuppliedIdempotencyKey_SurvivesQueueingAndEveryReplayAttempt()
    {
        var store = new InMemorySyncStore();
        using (var client = new HttpClient(BuildOfflineHandler(store)))
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/items")
            {
                Content = new StringContent("{}"),
            };
            request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, "order-42");
            await client.SendAsync(request);
        }

        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal("order-42", queued.RequestHeaders[IdempotencyKeyHeader]);

        // Fail the first attempt so the envelope is retried, then confirm the second
        // attempt carries the identical key rather than a fresh one.
        var keysSeen = new List<string>();
        var attempt = 0;
        var transport = new StubHttpMessageHandler(req =>
        {
            attempt++;
            keysSeen.Add(req.Headers.GetValues(IdempotencyKeyHeader).Single());
            return new HttpResponseMessage(
                attempt == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        });

        await using var orchestrator = BuildOrchestrator(store, transport);
        await orchestrator.FlushAsync();

        var deferred = Assert.Single(await store.GetPendingOutboxAsync());
        deferred.NextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.UpsertAsync(deferred);

        await orchestrator.FlushAsync();

        Assert.Equal(["order-42", "order-42"], keysSeen);
    }

    [Fact]
    public async Task ReadRequests_AreLeftAlone()
    {
        var (handler, stub) = BuildOnlineHandler();
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/api/items");

        Assert.False(stub.LastRequest!.Headers.Contains(IdempotencyKeyHeader));
    }
}
