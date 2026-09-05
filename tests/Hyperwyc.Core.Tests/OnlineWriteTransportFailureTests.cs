using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using System.Text;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// The connectivity service says online and the transport disagrees. The transport is the one
/// that knows: if no connection was established, nothing was sent, and the write belongs in the
/// outbox exactly as if <see cref="IConnectivityService"/> had reported offline in the first
/// place.
/// </summary>
/// <remarks>
/// Without this, a wrong connectivity answer — a captive portal, a VPN interface, a signal that
/// dropped between the check and the send, or <c>AlwaysOnlineConnectivityService</c> — costs the
/// write rather than an attempt. Reads already degraded to the cache on the same signal; writes
/// did not, and that asymmetry is what these cover.
/// </remarks>
public class OnlineWriteTransportFailureTests
{
    private static HyperwycHandler BuildHandler(
        InMemoryStore store,
        HttpMessageHandler transport,
        HyperwycEventStream? events = null,
        HyperwycOptions? options = null) =>
        new(
            store,
            new FakeConnectivityService(isConnected: true),
            events ?? new HyperwycEventStream(),
            options ?? new HyperwycOptions(),
            TestHealth())
        {
            InnerHandler = transport,
        };

    private static StubHttpMessageHandler Throwing(HttpRequestError error) =>
        new((Func<HttpRequestMessage, HttpResponseMessage>)(
            _ => throw new HttpRequestException(error, "the transport could not connect")));

    // -------------------------------------------------------------------------
    // The connection was never made, so the write is taken into custody
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ProxyTunnelError)]
    public async Task ConnectionNeverEstablished_QueuesTheWriteAndAnswers202(HttpRequestError error)
    {
        var store = new InMemoryStore();
        using var client = new HttpClient(BuildHandler(store, Throwing(error)));

        var response = await client.PostAsync("https://example.com/api/orders",
            new StringContent("""{"id":1}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Queued", response.Headers.GetValues("X-Hyperwyc-Status").Single());

        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal("POST", queued.Method);
        Assert.Equal("""{"id":1}""", Encoding.UTF8.GetString(queued.RequestBody!));
    }

    [Fact]
    public async Task QueuedWrite_IsIndistinguishableFromOneMadeOffline()
    {
        // The same situation reached by a different route must produce the same answer, or
        // correctness would depend on whether the connectivity service happened to be right.
        var store = new InMemoryStore();
        using var client = new HttpClient(
            BuildHandler(store, Throwing(HttpRequestError.ConnectionError)));

        var response = await client.PostAsync("https://example.com/api/orders",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Hyperwyc-Correlation-Id"));
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task QueuedWrite_PublishesOnQueued()
    {
        var store = new InMemoryStore();
        var events = new HyperwycEventStream();
        var received = new List<HyperwycEvent>();
        events.Subscribe(new DelegateObserver(received.Add));

        using var client = new HttpClient(
            BuildHandler(store, Throwing(HttpRequestError.NameResolutionError), events));

        await client.PostAsync("https://example.com/api/orders", new StringContent("{}"));

        var queued = Assert.Single(received, e => e.Type == HyperwycEventType.OnQueued);
        Assert.Equal("https://example.com/api/orders", queued.Url);
    }

    // -------------------------------------------------------------------------
    // The connection was made, so nothing can be assumed about the request
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpRequestError.InvalidResponse)]
    [InlineData(HttpRequestError.ResponseEnded)]
    [InlineData(HttpRequestError.HttpProtocolError)]
    [InlineData(HttpRequestError.Unknown)]
    public async Task ConnectionWasMade_RethrowsAndQueuesNothing(HttpRequestError error)
    {
        // A connection existed, so the server may well have processed the request. Queueing it
        // would risk a duplicate on a guess, and the guess is not needed: these are not failures
        // a later connectivity signal would fix.
        var store = new InMemoryStore();
        using var client = new HttpClient(BuildHandler(store, Throwing(error)));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PostAsync("https://example.com/api/orders", new StringContent("{}")));

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task NetworkOnlyRoute_RethrowsAndQueuesNothing()
    {
        // The route declared that deferring a write is the wrong answer. That holds however
        // Hyperwyc arrives at the offline state.
        var options = new HyperwycOptions();
        options.Routes.For("/api/payments/*", RoutePolicy.NetworkOnly());

        var store = new InMemoryStore();
        using var client = new HttpClient(
            BuildHandler(store, Throwing(HttpRequestError.ConnectionError), options: options));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PostAsync("https://example.com/api/payments/1", new StringContent("{}")));

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task Read_IsNotQueued()
    {
        // Reads have their own answer to this — serve the cache, or report no data. Either
        // way a GET must never end up in the outbox. See ReadTransportFailureTests.
        var store = new InMemoryStore();
        using var client = new HttpClient(
            BuildHandler(store, Throwing(HttpRequestError.ConnectionError)));

        var response = await client.GetAsync("https://example.com/api/orders");

        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    private sealed class DelegateObserver(Action<HyperwycEvent> onNext) : IObserver<HyperwycEvent>
    {
        public void OnNext(HyperwycEvent value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
