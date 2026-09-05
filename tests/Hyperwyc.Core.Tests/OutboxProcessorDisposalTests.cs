using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers <see cref="OutboxProcessor"/> disposal. Before issue #33 the orchestrator
/// implemented only <see cref="IAsyncDisposable"/>, so disposing a service provider
/// synchronously threw once it had been resolved, and <c>DisposeAsync</c> waited out
/// the entire retry budget.
/// </summary>
public class OutboxProcessorDisposalTests
{
    private static OutboxProcessor BuildOrchestrator(
        InMemoryStore store,
        HttpMessageHandler transport) =>
        new(
            store,
            new FakeConnectivityService(isConnected: true),
            new HyperwycEventStream(),
            new HyperwycOptions(),
            transport);

    private static Envelope OutboxEnvelope(string url = "https://example.com/api/orders") =>
        new() { Url = url, Method = "POST" };

    /// <summary>
    /// A transport that blocks until released, so a flush can be caught mid-send.
    /// </summary>
    private sealed class BlockingTransport : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>
    /// Blocks until cancelled, then takes a measurable moment to unwind, so that
    /// "returned before the flush finished" and "returned after" are distinguishable
    /// without depending on scheduling luck.
    /// </summary>
    private sealed class SlowUnwindTransport : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public volatile bool Unwound;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                Unwound = true;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // -------------------------------------------------------------------------
    // The reported defect: synchronous container disposal
    // -------------------------------------------------------------------------

    [Fact]
    public void ServiceProvider_DisposedSynchronously_AfterResolvingProcessor_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>(
            o => o.Connectivity = new AlwaysOnlineConnectivityService());
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<OutboxProcessor>();

        // This is what threw before issue #33.
        sp.Dispose();
    }

    // -------------------------------------------------------------------------
    // Neither path waits for queued work to finish sending
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_MidFlush_LeavesEnvelopesQueuedAndDoesNotDeadLetter()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(OutboxEnvelope());

        var transport = new BlockingTransport();
        var orchestrator = BuildOrchestrator(store, transport);

        var flush = orchestrator.FlushAsync();
        await transport.Entered;

        orchestrator.Dispose();

        // The flush unwinds via cancellation rather than completing.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);

        // Nothing lost: the envelope is still queued for the next start, and was
        // not mistaken for a delivery failure.
        var pending = await store.GetPendingOutboxAsync();
        Assert.Single(pending);
        Assert.False(pending[0].IsDeadLettered);
    }

    [Fact]
    public async Task DisposeAsync_MidFlush_ReturnsWithoutWaitingOutTheRetryBudget()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(OutboxEnvelope());

        var transport = new BlockingTransport();
        // A budget that would take minutes to exhaust if disposal waited for it.
        var orchestrator = BuildOrchestrator(store, transport);

        var flush = orchestrator.FlushAsync();
        await transport.Entered;

        var stopwatch = Stopwatch.StartNew();
        await orchestrator.DisposeAsync();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"DisposeAsync took {stopwatch.Elapsed}, suggesting it waited on the retry budget.");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
    }

    // The two paths differ in exactly one observable way: whether they return
    // before or after the in-flight flush has finished unwinding. SlowUnwindTransport
    // makes that window wide enough to assert on without racing.

    [Fact]
    public async Task Dispose_ReturnsBeforeTheFlushHasFinishedUnwinding()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(OutboxEnvelope());

        var transport = new SlowUnwindTransport();
        var orchestrator = BuildOrchestrator(store, transport);

        var flush = orchestrator.FlushAsync();
        await transport.Entered;

        orchestrator.Dispose();

        Assert.False(transport.Unwound);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
    }

    [Fact]
    public async Task DisposeAsync_ReturnsOnlyAfterTheFlushHasUnwound()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(OutboxEnvelope());

        var transport = new SlowUnwindTransport();
        var orchestrator = BuildOrchestrator(store, transport);

        var flush = orchestrator.FlushAsync();
        await transport.Entered;

        await orchestrator.DisposeAsync();

        // Reacquiring the gate is only possible once the flush released it, which
        // happens after the transport has fully unwound.
        Assert.True(transport.Unwound);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
    }

    // -------------------------------------------------------------------------
    // Cancellation reaches every kind of flush
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Dispose_CancelsAManualFlushThatPassedNoToken()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(OutboxEnvelope());

        var transport = new BlockingTransport();
        var orchestrator = BuildOrchestrator(store, transport);

        // FlushAsync() with no token — a "sync now" button. Before issue #33 the
        // orchestrator had no way to cancel this.
        var flush = orchestrator.FlushAsync();
        await transport.Entered;

        orchestrator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);
    }

    // -------------------------------------------------------------------------
    // Idempotency and post-disposal behaviour
    // -------------------------------------------------------------------------

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var orchestrator = BuildOrchestrator(new InMemoryStore(), new BlockingTransport());

        orchestrator.Dispose();
        orchestrator.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_AfterDispose_DoesNotThrow()
    {
        var orchestrator = BuildOrchestrator(new InMemoryStore(), new BlockingTransport());

        orchestrator.Dispose();
        await orchestrator.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_AfterDisposeAsync_DoesNotThrow()
    {
        var orchestrator = BuildOrchestrator(new InMemoryStore(), new BlockingTransport());

        await orchestrator.DisposeAsync();
        orchestrator.Dispose();
    }

    [Fact]
    public async Task FlushAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var orchestrator = BuildOrchestrator(new InMemoryStore(), new BlockingTransport());
        orchestrator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => orchestrator.FlushAsync());
    }

    [Fact]
    public async Task FlushAsync_AfterDisposeAsync_ThrowsObjectDisposed()
    {
        var orchestrator = BuildOrchestrator(new InMemoryStore(), new BlockingTransport());
        await orchestrator.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => orchestrator.FlushAsync());
    }
}
