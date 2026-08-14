using System.Net;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue #38: Hyperwyc retries connectivity failures when connectivity changes,
/// rather than running a generic backoff loop inside a flush.
/// </summary>
/// <remarks>
/// Previously every non-2xx was retried in place — a rejected write spent its whole
/// budget (about a minute on defaults) before dead-lettering, and because the outbox
/// drains sequentially it held up everything queued behind it.
/// </remarks>
public class RetryModelTests
{
    private static SyncOrchestrator BuildOrchestrator(
        InMemorySyncStore store,
        HttpMessageHandler transport,
        RetryOptions? retryOptions = null,
        SyncEventStream? events = null) =>
        new(
            store,
            new FakeSyncPolicy(retryOptions: retryOptions ?? new RetryOptions(
                MaxRetries: 3,
                InitialDelay: TimeSpan.FromHours(1),   // far enough out that no follow-up interferes
                BackoffMultiplier: 2.0)),
            new FakeConnectivityService(isConnected: true),
            events ?? new SyncEventStream(),
            new HyperwycOptions { ConnectivityDebounceDelay = TimeSpan.Zero },
            transport);

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
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox());
        var transport = new CountingTransport(status);
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(1, transport.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task PermanentFailure_PublishesOnFailed_NotOnRetrying()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox());
        var events = new SyncEventStream();
        var received = new List<SyncEvent>();
        events.Subscribe(new DelegateObserver<SyncEvent>(received.Add));

        await using var orchestrator = BuildOrchestrator(
            store, new CountingTransport(HttpStatusCode.Conflict), events: events);

        await orchestrator.FlushAsync();

        Assert.Contains(received, e => e.Type == SyncEventType.OnFailed);
        Assert.DoesNotContain(received, e => e.Type == SyncEventType.OnRetrying);
    }

    // -------------------------------------------------------------------------
    // The reason this matters: one bad write must not starve the good ones
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ARejectedWrite_DoesNotDelayOrBlockTheOnesBehindIt()
    {
        var store = new InMemorySyncStore();
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
    // Transient failures defer rather than retry in place
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TransientFailure_LeavesEnvelopeQueuedWithRetryScheduled()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox());
        var transport = new CountingTransport(HttpStatusCode.ServiceUnavailable);
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(1, transport.CallCount);

        var pending = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.False(pending.IsDeadLettered);
        Assert.Equal(1, pending.RetryCount);
        Assert.NotNull(pending.NextRetryUtc);

        // Deferred, so not eligible again until its scheduled time.
        Assert.Empty(await store.GetReadyToSendAsync(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task TransientFailure_DoesNotDelayTheNextEnvelopeInTheFlush()
    {
        var store = new InMemorySyncStore();
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

    [Fact]
    public async Task TransientFailure_DeadLettersOnceTheBudgetIsSpent()
    {
        var store = new InMemorySyncStore();
        var envelope = Outbox();
        envelope.RetryCount = 3;   // budget is 3; the next failure exceeds it
        await store.UpsertAsync(envelope);

        var transport = new CountingTransport(HttpStatusCode.ServiceUnavailable);
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task RetryBudget_SurvivesANewOrchestrator()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox());
        var transport = new CountingTransport(HttpStatusCode.ServiceUnavailable);

        await using (var first = BuildOrchestrator(store, transport))
            await first.FlushAsync();

        var afterFirst = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal(1, afterFirst.RetryCount);

        // A second orchestrator — standing in for the process having been killed and
        // restarted — continues the budget rather than starting it over.
        afterFirst.NextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.UpsertAsync(afterFirst);

        await using (var second = BuildOrchestrator(store, transport))
            await second.FlushAsync();

        var afterSecond = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal(2, afterSecond.RetryCount);
    }

    // -------------------------------------------------------------------------
    // A dead network ends the flush
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TransportFailure_AbandonsTheRestOfTheFlush()
    {
        var store = new InMemorySyncStore();
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
    public async Task TransportFailure_DoesNotConsumeRetryBudgetOrDeadLetter()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox());
        await using var orchestrator = BuildOrchestrator(store, new ThrowingTransport());

        await orchestrator.FlushAsync();

        var pending = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.False(pending.IsDeadLettered);

        // Being unable to reach the network says nothing about the request, so it is
        // not held against it.
        Assert.Equal(0, pending.RetryCount);
        Assert.Null(pending.NextRetryUtc);
    }

    [Fact]
    public async Task TransportFailure_LeavesEnvelopesImmediatelyEligibleAgain()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox());
        var throwing = new ThrowingTransport();

        await using (var orchestrator = BuildOrchestrator(store, throwing))
            await orchestrator.FlushAsync();

        // Nothing was deferred, so the next connectivity signal retries straight away.
        Assert.Single(await store.GetReadyToSendAsync(DateTimeOffset.UtcNow));

        var working = new CountingTransport(HttpStatusCode.OK);
        await using (var orchestrator = BuildOrchestrator(store, working))
            await orchestrator.FlushAsync();

        Assert.Equal(1, working.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }
}
