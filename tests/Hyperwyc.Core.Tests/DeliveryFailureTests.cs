using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers what a flush does with each class of failure. Issue #38 settled the classification;
/// ADR 0004 then removed the retry apparatus that had survived it.
/// </summary>
/// <remarks>
/// There is no retry budget, no backoff and no scheduled follow-up. A write the server refuses
/// is dead-lettered on the first attempt; anything else stays in the outbox and is tried again
/// on the next flush, which happens on connectivity restored or application start.
/// </remarks>
public class DeliveryFailureTests
{
    private static OutboxProcessor BuildOrchestrator(
        InMemoryStore store,
        HttpMessageHandler transport,
        HyperwycEventStream? events = null) =>
        new(            store,
            new FakeConnectivityService(isConnected: true),
            events ?? new HyperwycEventStream(),
            new HyperwycOptions(),
            transport,
            TestHealth());

    private static Envelope Outbox(string url = "https://example.com/api/orders") =>
        new() { Url = url, Method = "POST" };

    private sealed class CountingTransport(HttpStatusCode status) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Urls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    // Local copy, matching the other orchestrator test files. There are now several
    // near-identical observers across the suite; worth consolidating into Fakes/ some time.
    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class ThrowingTransport : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("no route to host"));
        }
    }

    // -------------------------------------------------------------------------
    // Permanent failures cost one attempt
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task PermanentFailure_DeadLettersOnTheFirstAttempt(HttpStatusCode status)
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        var transport = new CountingTransport(status);
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(1, transport.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task PermanentFailure_PublishesOnFailed()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        var events = new HyperwycEventStream();
        var received = new List<HyperwycEvent>();
        events.Subscribe(new DelegateObserver<HyperwycEvent>(received.Add));

        await using var orchestrator = BuildOrchestrator(
            store, new CountingTransport(HttpStatusCode.Conflict), events: events);

        await orchestrator.FlushAsync();

        // One attempt, one event. There is no retry to announce.
        Assert.Single(received, e => e.Type == HyperwycEventType.OnFailed);
    }

    // -------------------------------------------------------------------------
    // The reason this matters: one bad write must not starve the good ones
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ARejectedWrite_DoesNotDelayOrBlockTheOnesBehindIt()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox("https://example.com/api/rejected"));
        await store.UpsertAsync(Outbox("https://example.com/api/fine"));

        // Rejects the first URL, accepts the second.
        var transport = new StubHttpMessageHandler(req =>
            new HttpResponseMessage(
                req.RequestUri!.ToString().Contains("rejected")
                    ? HttpStatusCode.Conflict
                    : HttpStatusCode.OK));

        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        // Both were attempted in the same flush — the rejected one did not consume a
        // retry budget the good one had to wait out.
        Assert.Equal(2, transport.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    // -------------------------------------------------------------------------
    // Transient failures stay in the outbox
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TransientFailure_LeavesTheEnvelopeQueuedForTheNextFlush()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        var transport = new CountingTransport(HttpStatusCode.ServiceUnavailable);
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(1, transport.CallCount);

        var pending = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.False(pending.IsDeadLettered);

        // The server answered, and not with a refusal — so the write is still live, and the
        // outcome recorded against it says why it has not gone yet.
        Assert.Equal(DeliveryOutcomeKind.TransientFailure, pending.LastOutcome?.Kind);
        Assert.Equal(503, pending.LastOutcome?.StatusCode);
    }

    [Fact]
    public async Task TransientFailure_IsRetriedByTheNextFlush_WithNoBudgetToExhaust()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        var failing = new CountingTransport(HttpStatusCode.ServiceUnavailable);

        // Well past what the old five-attempt budget allowed. Nothing is counted, so nothing
        // runs out: Hyperwyc keeps the write until the server takes it or the app discards it.
        await using (var orchestrator = BuildOrchestrator(store, failing))
        {
            for (var i = 0; i < 8; i++)
                await orchestrator.FlushAsync();
        }

        Assert.Equal(8, failing.CallCount);
        Assert.Single(await store.GetPendingOutboxAsync());

        var working = new CountingTransport(HttpStatusCode.OK);
        await using (var orchestrator = BuildOrchestrator(store, working))
            await orchestrator.FlushAsync();

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task TransientFailure_DoesNotDelayTheNextEnvelopeInTheFlush()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox("https://example.com/api/flaky"));
        await store.UpsertAsync(Outbox("https://example.com/api/fine"));

        var transport = new StubHttpMessageHandler(req =>
            new HttpResponseMessage(
                req.RequestUri!.ToString().Contains("flaky")
                    ? HttpStatusCode.ServiceUnavailable
                    : HttpStatusCode.OK));

        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(2, transport.CallCount);

        // The good one is gone; the flaky one is deferred, not dead-lettered.
        var pending = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Contains("flaky", pending.Url);
    }



    // -------------------------------------------------------------------------
    // A dead network ends the flush
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TransportFailure_AbandonsTheRestOfTheFlush()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox("https://example.com/api/one"));
        await store.UpsertAsync(Outbox("https://example.com/api/two"));
        await store.UpsertAsync(Outbox("https://example.com/api/three"));

        var transport = new ThrowingTransport();
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        // One attempt establishes the network is unusable; the other two would fail
        // identically, so they are not tried.
        Assert.Equal(1, transport.CallCount);
        Assert.Equal(3, (await store.GetPendingOutboxAsync()).Count);
    }

    [Fact]
    public async Task TransportFailure_DoesNotDeadLetter()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        await using var orchestrator = BuildOrchestrator(store, new ThrowingTransport());

        await orchestrator.FlushAsync();

        var pending = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.False(pending.IsDeadLettered);

        // Being unable to reach the network says nothing about the request.
        Assert.Equal(DeliveryOutcomeKind.TransportFailure, pending.LastOutcome?.Kind);
        Assert.Null(pending.LastOutcome?.StatusCode);
    }

    [Fact]
    public async Task TransportFailure_LeavesEnvelopesEligibleForTheNextFlush()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        var throwing = new ThrowingTransport();

        await using (var orchestrator = BuildOrchestrator(store, throwing))
            await orchestrator.FlushAsync();

        // Nothing was held back, so the next connectivity signal tries straight away.
        Assert.Single(await store.GetPendingOutboxAsync());

        var working = new CountingTransport(HttpStatusCode.OK);
        await using (var orchestrator = BuildOrchestrator(store, working))
            await orchestrator.FlushAsync();

        Assert.Equal(1, working.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }
}
