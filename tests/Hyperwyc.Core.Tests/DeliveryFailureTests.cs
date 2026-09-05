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
/// There is no retry budget, no backoff and no scheduled follow-up. Any answer from the server
/// is a final outcome — the request reached the API, which was the job. Only a transport
/// failure, where no response came back at all, leaves the envelope in the outbox.
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
    // Any answer from the server is final
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]   // 503 — the server is unwell
    [InlineData(HttpStatusCode.InternalServerError)]  // 500
    [InlineData(HttpStatusCode.RequestTimeout)]       // 408 — once carved out as retryable
    [InlineData(HttpStatusCode.TooManyRequests)]      // 429 — likewise
    public async Task AnyResponse_IsFinal_EvenOnesThatLookRetryable(HttpStatusCode status)
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        var transport = new CountingTransport(status);
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        // The server answered, so the request reached the API and Hyperwyc's job is done.
        // Whether to try again needs information Hyperwyc does not have, and its only retry
        // trigger — a connectivity change — has nothing to do with a server recovering.
        Assert.Equal(1, transport.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task ServerError_IsNotAttemptedAgainByALaterFlush()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        var failing = new CountingTransport(HttpStatusCode.ServiceUnavailable);

        await using (var orchestrator = BuildOrchestrator(store, failing))
        {
            for (var i = 0; i < 8; i++)
                await orchestrator.FlushAsync();
        }

        // One attempt, not eight. Keeping it queued would promise a retry on an event that may
        // never come — a device that never goes offline again never flushes again.
        Assert.Equal(1, failing.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task ServerError_RecordsTheOutcomeItDeadLetteredOn()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        var events = new HyperwycEventStream();
        var received = new List<HyperwycEvent>();
        events.Subscribe(new DelegateObserver<HyperwycEvent>(received.Add));

        await using var orchestrator = BuildOrchestrator(
            store, new CountingTransport(HttpStatusCode.ServiceUnavailable), events: events);

        await orchestrator.FlushAsync();

        // Dead-lettering is not discarding: the status the server gave is reported, because it
        // is what the application needs in order to decide whether to raise it again itself.
        var failed = Assert.Single(received, e => e.Type == HyperwycEventType.OnFailed);
        Assert.Equal(DeliveryOutcomeKind.Rejected, failed.Outcome?.Kind);
        Assert.Equal(503, failed.Outcome?.StatusCode);
    }

    [Fact]
    public async Task ServerError_DoesNotStopTheFlush()
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

        // A server answering at all proves the network is up, so the rest of the flush stands.
        // Only a transport failure ends it.
        Assert.Equal(2, transport.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
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
