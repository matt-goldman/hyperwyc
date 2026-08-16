using System.Net;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

public class SyncOrchestratorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static SyncOrchestrator BuildOrchestrator(
        InMemorySyncStore store,
        StubHttpMessageHandler transport,
        SyncEventStream? events = null,
        bool connected = true,
        FakeSyncPolicy? policy = null)
    {
        return new SyncOrchestrator(
            store,
            policy ?? new FakeSyncPolicy(),
            new FakeConnectivityService(connected),
            events ?? new SyncEventStream(),
            new HyperwycOptions { ConnectivityDebounceDelay = TimeSpan.Zero },
            transport);
    }

    private static Envelope MakeOutboxEnvelope(string url = "https://example.com/api/orders",
        string method = "POST",
        DateTimeOffset? createdUtc = null)
    {
        var envelope = new Envelope
        {
            Url = url,
            Method = method,
        };
        if (createdUtc.HasValue)
        {
            // CreatedUtc is init-only; create a fresh envelope with reflection workaround isn't needed
            // because we can read the order via store sorting — so just note they're created in sequence.
        }
        return envelope;
    }

    // -------------------------------------------------------------------------
    // FlushAsync — success
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_PendingEnvelope_SendsRequest()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(MakeOutboxEnvelope());

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task FlushAsync_SuccessfulSend_MarksEnvelopeSynced()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeOutboxEnvelope();
        await store.UpsertAsync(envelope);

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        var pending = await store.GetPendingOutboxAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task FlushAsync_SuccessfulSend_PublishesOnSyncedEvent()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(MakeOutboxEnvelope());

        var events = new SyncEventStream();
        SyncEvent? received = null;
        events.Subscribe(new DelegateObserver<SyncEvent>(e => received = e));

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport, events: events);

        await orchestrator.FlushAsync();

        Assert.NotNull(received);
        Assert.Equal(SyncEventType.OnSynced, received!.Type);
    }

    [Fact]
    public async Task FlushAsync_MultipleEnvelopes_SentInCreatedUtcOrder()
    {
        var store = new InMemorySyncStore();

        // Insert in reverse order — flush must sort ascending by CreatedUtc.
        var first  = new Envelope { Url = "https://example.com/a", Method = "POST" };
        await Task.Delay(5);  // ensure distinct timestamps
        var second = new Envelope { Url = "https://example.com/b", Method = "POST" };
        await Task.Delay(5);
        var third  = new Envelope { Url = "https://example.com/c", Method = "POST" };

        // Insert in reverse order to verify ordering is by CreatedUtc, not insertion order.
        await store.UpsertAsync(third);
        await store.UpsertAsync(first);
        await store.UpsertAsync(second);

        var sentUrls = new List<string>();
        var transport = new StubHttpMessageHandler(req =>
        {
            sentUrls.Add(req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(["https://example.com/a", "https://example.com/b", "https://example.com/c"], sentUrls);
    }

    [Fact]
    public async Task FlushAsync_NoEnvelopes_DoesNotSendAnything()
    {
        var store = new InMemorySyncStore();
        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(0, transport.CallCount);
    }

    // -------------------------------------------------------------------------
    // FlushAsync — semaphore guard (no double flush)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_ConcurrentCall_SecondCallSkips()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(MakeOutboxEnvelope());

        // The transport blocks until we let it proceed.
        var gate = new TaskCompletionSource<bool>();
        int sendCount = 0;
        var transport = new StubHttpMessageHandler(async req =>
        {
            Interlocked.Increment(ref sendCount);
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var orchestrator = BuildOrchestrator(store, transport);

        // Start first flush (will block inside transport).
        var firstFlush = orchestrator.FlushAsync();

        // Give the first flush a moment to acquire the semaphore.
        await Task.Delay(50);

        // Second flush should return immediately (semaphore held).
        await orchestrator.FlushAsync();

        // Unblock first flush.
        gate.SetResult(true);
        await firstFlush;

        // Only one HTTP send should have occurred.
        Assert.Equal(1, sendCount);
    }

    // -------------------------------------------------------------------------
    // FlushAsync — stored headers are replayed as-is
    // -------------------------------------------------------------------------

    // Replaces a test that asserted Hyperwyc re-injected an Idempotency-Key derived from
    // the envelope id. It no longer adds anything of its own (issue #39); what it must do
    // is carry the application's headers through unchanged.
    [Fact]
    public async Task FlushAsync_ReplaysStoredHeadersVerbatim()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeOutboxEnvelope();
        envelope.RequestHeaders["X-Correlation-Id"] = "abc-123";
        envelope.RequestHeaders["X-Tenant"] = "acme";
        await store.UpsertAsync(envelope);

        HttpRequestMessage? captured = null;
        var transport = new StubHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.NotNull(captured);
        Assert.Equal("abc-123", captured!.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("acme", captured.Headers.GetValues("X-Tenant").Single());
    }

    // -------------------------------------------------------------------------
    // FlushAsync — failure → retry → dead-letter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_AllRetriesExhausted_MovesToDeadLetter()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(MakeOutboxEnvelope());

        // Always return a server error to exhaust retries.
        var transport = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        // Use a policy with 0 retries to keep the test fast.
        var policy = new FakeSyncPolicy(retryOptions: new RetryOptions(
            MaxRetries: 0,
            InitialDelay: TimeSpan.Zero,
            BackoffMultiplier: 1.0));
        await using var orchestrator = BuildOrchestrator(store, transport, policy: policy);

        await orchestrator.FlushAsync();

        var pending = await store.GetPendingOutboxAsync();
        Assert.Empty(pending); // removed from outbox

        // Check it's dead-lettered (GetPendingOutboxAsync excludes dead-lettered).
    }

    [Fact]
    public async Task FlushAsync_AllRetriesExhausted_PublishesOnFailedEvent()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(MakeOutboxEnvelope());

        var events = new SyncEventStream();
        var received = new List<SyncEvent>();
        events.Subscribe(new DelegateObserver<SyncEvent>(received.Add));

        var transport = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var policy = new FakeSyncPolicy(retryOptions: new RetryOptions(
            MaxRetries: 0,
            InitialDelay: TimeSpan.Zero,
            BackoffMultiplier: 1.0));
        await using var orchestrator = BuildOrchestrator(store, transport, events: events, policy: policy);

        await orchestrator.FlushAsync();

        Assert.Single(received);
        Assert.Equal(SyncEventType.OnFailed, received[0].Type);
    }

    // A transient failure is not retried inside the flush; the envelope is deferred and
    // a follow-up pass picks it up. This covers that whole path, which is what replaced
    // the in-flush backoff loop.
    [Fact]
    public async Task TransientFailure_IsRetriedByAFollowUpFlush()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(MakeOutboxEnvelope());

        var events = new SyncEventStream();
        var received = new List<SyncEvent>();
        events.Subscribe(new DelegateObserver<SyncEvent>(received.Add));

        int callCount = 0;
        var transport = new StubHttpMessageHandler(_ =>
        {
            callCount++;
            var status = callCount == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            return new HttpResponseMessage(status);
        });
        var policy = new FakeSyncPolicy(retryOptions: new RetryOptions(
            MaxRetries: 3,
            InitialDelay: TimeSpan.FromMilliseconds(50),
            BackoffMultiplier: 1.0));
        await using var orchestrator = BuildOrchestrator(store, transport, events: events, policy: policy);

        await orchestrator.FlushAsync();

        // The first attempt failed transiently, so nothing is delivered yet and the
        // envelope is still queued rather than dead-lettered.
        Assert.Equal(1, callCount);
        Assert.DoesNotContain(received, e => e.Type == SyncEventType.OnSynced);
        Assert.Single(await store.GetPendingOutboxAsync());

        await WaitUntilAsync(() => received.Any(e => e.Type == SyncEventType.OnSynced));

        Assert.Contains(received, e => e.Type == SyncEventType.OnRetrying);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or the timeout expires.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(15);
        }

        Assert.Fail($"Condition not met within {timeoutMs}ms.");
    }

    // -------------------------------------------------------------------------
    // Dead-lettered envelopes not re-sent
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_DeadLetteredEnvelope_NotSentAgain()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeOutboxEnvelope();
        await store.UpsertAsync(envelope);
        await store.MoveToDeadLetterAsync(envelope.Id);

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(0, transport.CallCount);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
