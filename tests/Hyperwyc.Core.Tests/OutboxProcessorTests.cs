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

    private static QueuedWrite Queued(
        string url = "https://example.com/api/orders",
        string method = "POST") =>
        new() { Url = url, Method = method };

    // -------------------------------------------------------------------------
    // FlushAsync — success
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_PendingWrite_SendsRequest()
    {
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Queued());

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task FlushAsync_SuccessfulSend_RemovesTheDeliveredWrite()
    {
        var store = new InMemoryStore();
        var write = Queued();
        await store.UpsertQueuedWriteAsync(write);

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
        await store.UpsertQueuedWriteAsync(Queued());

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
    public async Task FlushAsync_MultipleWrites_SentInCreatedUtcOrder()
    {
        var store = new InMemoryStore();

        // Insert in reverse order — flush must sort ascending by CreatedUtc.
        var first  = new QueuedWrite { Url = "https://example.com/a", Method = "POST" };
        await Task.Delay(5);  // ensure distinct timestamps
        var second = new QueuedWrite { Url = "https://example.com/b", Method = "POST" };
        await Task.Delay(5);
        var third  = new QueuedWrite { Url = "https://example.com/c", Method = "POST" };

        // Insert in reverse order to verify ordering is by CreatedUtc, not insertion order.
        await store.UpsertQueuedWriteAsync(third);
        await store.UpsertQueuedWriteAsync(first);
        await store.UpsertQueuedWriteAsync(second);

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
    public async Task FlushAsync_NothingQueued_DoesNotSendAnything()
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
        await store.UpsertQueuedWriteAsync(Queued());

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
    // the write's id. It no longer adds anything of its own (issue #39); what it must do
    // is carry the application's headers through unchanged.
    [Fact]
    public async Task FlushAsync_ReplaysStoredHeadersVerbatim()
    {
        var store = new InMemoryStore();
        var write = Queued();
        write.RequestHeaders["X-Correlation-Id"] = "abc-123";
        write.RequestHeaders["X-Tenant"] = "acme";
        await store.UpsertQueuedWriteAsync(write);

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
    // Delivered writes not re-sent
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FlushAsync_DeliveredWrite_NotSentAgain()
    {
        var store = new InMemoryStore();
        var write = Queued();
        await store.UpsertQueuedWriteAsync(write);
        await store.RemoveDeliveredAsync(write.Id);

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
        await store.PutCachedResponseAsync(CachedGet("https://example.com/api/orders"));
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/orders"));

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
        await store.PutCachedResponseAsync(CachedGet("https://example.com/api/orders"));
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/orders"));

        var transport = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        await using var orchestrator = BuildOrchestrator(store, transport);

        await orchestrator.FlushAsync();

        Assert.NotNull(await store.GetCachedResponseAsync("https://example.com/api/orders"));

        // Still delivered, though: the write is gone either way.
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CachedResponse CachedGet(string url) =>
        new()
        {
            Url = url,
            StatusCode = 200,
            Headers = [],
            Body = "cached"u8.ToArray(),
            CachedAt = DateTimeOffset.UtcNow,
        };

    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
