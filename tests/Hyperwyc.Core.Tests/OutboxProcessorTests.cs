using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

public class OutboxProcessorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static OutboxProcessor BuildOrchestrator(
        InMemoryStore store,
        StubHttpMessageHandler transport,
        HyperwycEventStream? events = null,
        bool connected = true)
    {
        return new OutboxProcessor(
            store,
            new FakeConnectivityService(connected),
            events ?? new HyperwycEventStream(),
            new HyperwycOptions(),
            transport, TestHealth());
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
        var store = new InMemoryStore();
        await store.UpsertAsync(MakeOutboxEnvelope());

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task FlushAsync_SuccessfulSend_MarksEnvelopeDelivered()
    {
        var store = new InMemoryStore();
        var envelope = MakeOutboxEnvelope();
        await store.UpsertAsync(envelope);

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        var pending = await store.GetPendingOutboxAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task FlushAsync_SuccessfulSend_PublishesOnDeliveredEvent()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(MakeOutboxEnvelope());

        var events = new HyperwycEventStream();
        HyperwycEvent? received = null;
        events.Subscribe(new DelegateObserver<HyperwycEvent>(e => received = e));

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport, events: events);

        await orchestrator.FlushAsync();

        Assert.NotNull(received);
        Assert.Equal(HyperwycEventType.OnDelivered, received!.Type);
    }

    [Fact]
    public async Task FlushAsync_MultipleEnvelopes_SentInCreatedUtcOrder()
    {
        var store = new InMemoryStore();

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
        var store = new InMemoryStore();
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
        var store = new InMemoryStore();
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
        var store = new InMemoryStore();
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
    // Delivered envelopes not re-sent
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_DeliveredEnvelope_NotSentAgain()
    {
        var store = new InMemoryStore();
        var envelope = MakeOutboxEnvelope();
        await store.UpsertAsync(envelope);
        await store.RemoveDeliveredAsync(envelope.Id);

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(0, transport.CallCount);
    }

    // -------------------------------------------------------------------------
    // Cache invalidation on a replayed write
    //
    // The one place a status code is still read after ADR 0010, and deliberately so: this is a
    // freshness judgement about Hyperwyc's own cache, not a judgement about whether Hyperwyc
    // succeeded. It follows RFC 9111 §4.4, which invalidates on a non-error response to an
    // unsafe method.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_SuccessfulWrite_InvalidatesTheCacheForItsPrefix()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedGet("https://example.com/api/orders"));
        await store.UpsertAsync(MakeOutboxEnvelope("https://example.com/api/orders"));

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Null(await store.GetCachedResponseAsync("https://example.com/api/orders"));
    }

    [Fact]
    public async Task FlushAsync_RejectedWrite_LeavesTheCacheAlone()
    {
        // A 422 says the write did not happen, so the cached reads under it are still good.
        // Dropping them would cost an offline read for nothing.
        var store = new InMemoryStore();
        await store.UpsertAsync(CachedGet("https://example.com/api/orders"));
        await store.UpsertAsync(MakeOutboxEnvelope("https://example.com/api/orders"));

        var transport = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.NotNull(await store.GetCachedResponseAsync("https://example.com/api/orders"));

        // Still delivered, though: the envelope is gone either way.
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Envelope CachedGet(string url)
    {
        var envelope = new Envelope { Url = url, Method = "GET", IsSynced = true };
        envelope.Response = new CachedResponse
        {
            StatusCode = 200,
            Headers = [],
            Body = "cached"u8.ToArray(),
            CachedAt = DateTimeOffset.UtcNow,
        };
        return envelope;
    }

    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
