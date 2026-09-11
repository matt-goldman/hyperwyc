using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Tests for the diagnostics read path added in issue #23. Covers the projection to
/// <see cref="PendingItem"/>, the processor helper that reads it, and the DI wiring that
/// exposes it as <see cref="IHyperwycDiagnostics"/>.
/// </summary>
public class DiagnosticsViewTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static OutboxProcessor BuildProcessor(
        InMemoryStore store,
        HttpMessageHandler? transport = null,
        bool connected = true) =>
        new(
            store,
            new FakeConnectivityService(connected),
            new HyperwycEventStream(),
            new HyperwycOptions(),
            transport ?? new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            TestHealth());

    private static QueuedWrite Queued(
        string url = "https://example.com/api/orders",
        string method = "POST",
        DateTimeOffset? createdUtc = null) =>
        new()
        {
            Url = url,
            Method = method,
            CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow,
        };

    // -------------------------------------------------------------------------
    // PendingItem.From — field mapping
    // -------------------------------------------------------------------------

    [Fact]
    public void From_MapsEveryField()
    {
        var write = new QueuedWrite
        {
            Url = "https://example.com/api/orders",
            Method = "PUT",
            CreatedUtc = new DateTimeOffset(2026, 3, 5, 10, 0, 0, TimeSpan.Zero),
            RetryCount = 4,
            LastOutcome = new DeliveryOutcome
            {
                Kind = DeliveryOutcomeKind.TransportFailure,
                Error = "no route to host",
                OccurredUtc = DateTimeOffset.UtcNow,
            },
        };

        var view = PendingItem.From(write);

        Assert.Equal(write.Id, view.Id);
        Assert.Equal(write.CorrelationId, view.CorrelationId);
        Assert.Equal("PUT", view.Method);
        Assert.Equal("https://example.com/api/orders", view.Url);
        Assert.Equal(write.CreatedUtc, view.CreatedUtc);
        Assert.Equal(4, view.RetryCount);
        Assert.Same(write.LastOutcome, view.LastOutcome);
    }

    [Fact]
    public void From_NoOutcomeYet_LeavesLastOutcomeNull()
    {
        // A fresh write that has not yet been attempted — the diagnostics view must reflect that
        // rather than fabricating an outcome.
        var view = PendingItem.From(Queued());

        Assert.Null(view.LastOutcome);
    }

    [Fact]
    public void From_ThrowsOnNullWrite()
    {
        Assert.Throws<ArgumentNullException>(() => PendingItem.From(null!));
    }

    // -------------------------------------------------------------------------
    // OutboxProcessor.GetDiagnosticViewAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetDiagnosticViewAsync_EmptyStore_ReturnsEmpty()
    {
        var store = new InMemoryStore();
        await using var processor = BuildProcessor(store);

        var view = await processor.GetDiagnosticViewAsync();

        Assert.Empty(view);
    }

    [Fact]
    public async Task GetDiagnosticViewAsync_PopulatedStore_ReturnsOneItemPerWrite()
    {
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/a"));
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/b"));
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/c"));
        await using var processor = BuildProcessor(store);

        var view = await processor.GetDiagnosticViewAsync();

        Assert.Equal(3, view.Count);
    }

    [Fact]
    public async Task GetDiagnosticViewAsync_ReturnsItemsInCreatedUtcOrder()
    {
        // The store guarantees CreatedUtc ordering; the view is a read-only projection, so it
        // must preserve that order rather than reshuffle it.
        var store = new InMemoryStore();
        var now = DateTimeOffset.UtcNow;
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/c", createdUtc: now.AddSeconds(2)));
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/a", createdUtc: now));
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/b", createdUtc: now.AddSeconds(1)));

        await using var processor = BuildProcessor(store);
        var view = await processor.GetDiagnosticViewAsync();

        Assert.Equal(
            ["https://example.com/api/a", "https://example.com/api/b", "https://example.com/api/c"],
            view.Select(v => v.Url));
    }

    [Fact]
    public async Task GetDiagnosticViewAsync_MixedStates_SurfacesRetryCountAndLastOutcome()
    {
        // A write with a transport failure behind it and a fresh one added afterwards. The whole
        // point of the view is that the first one explains itself — RetryCount and LastOutcome
        // must be populated, not silently zeroed out (which is what the initial hardcoded 0 in
        // GetDiagnosticView did). The fresh one, added after the flush ended, has never been
        // attempted, so its counters must stay at their defaults.
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/attempted"));

        var failing = new ThrowingTransport();
        await using (var processor = BuildProcessor(store, failing))
            await processor.FlushAsync();

        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/fresh"));

        await using var reader = BuildProcessor(store);
        var view = await reader.GetDiagnosticViewAsync();

        var attempted = Assert.Single(view, v => v.Url == "https://example.com/api/attempted");
        Assert.Equal(1, attempted.RetryCount);
        Assert.Equal(DeliveryOutcomeKind.TransportFailure, attempted.LastOutcome?.Kind);

        var fresh = Assert.Single(view, v => v.Url == "https://example.com/api/fresh");
        Assert.Equal(0, fresh.RetryCount);
        Assert.Null(fresh.LastOutcome);
    }

    [Fact]
    public async Task GetDiagnosticViewAsync_RetryCount_IncrementsOnEachTransportFailure()
    {
        // Two separate flushes, both failing at the transport, must accumulate.
        var store = new InMemoryStore();
        var write = Queued();
        await store.UpsertQueuedWriteAsync(write);

        var failing = new ThrowingTransport();
        await using (var processor = BuildProcessor(store, failing))
        {
            await processor.FlushAsync();
            await processor.FlushAsync();
        }

        await using var reader = BuildProcessor(store);
        var view = Assert.Single(await reader.GetDiagnosticViewAsync());
        Assert.Equal(2, view.RetryCount);
    }

    [Fact]
    public async Task GetDiagnosticViewAsync_StoreFailure_ReportsAndReturnsEmpty()
    {
        // Matches FlushAsync: an unreadable store is reported through StoreHealth and the caller
        // gets nothing, rather than the raw exception. A diagnostics UI learns the store is
        // broken through the OnStoreUnreadable event.
        var events = new HyperwycEventStream();
        var received = new List<HyperwycEvent>();
        events.Subscribe(new DelegateObserver<HyperwycEvent>(received.Add));

        var store = new ThrowingStore();
        var health = new StoreHealth(store, new HyperwycOptions(), events);
        await using var processor = new OutboxProcessor(
            store,
            new FakeConnectivityService(isConnected: true),
            events,
            new HyperwycOptions(),
            new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            health);

        var view = await processor.GetDiagnosticViewAsync();

        Assert.Empty(view);
        Assert.False(health.IsUsable);
        Assert.Contains(received, e => e.Type == HyperwycEventType.OnStoreUnreadable);
    }

    // -------------------------------------------------------------------------
    // DI wiring
    //
    // A single HyperwycService instance backs both interfaces. Resolving one via a factory
    // that news up another would give two instances, each with its own view of the outbox
    // — which was the shape of the initial registration and would have made this feature
    // fail on first use.
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwycCore_RegistersIHyperwycDiagnostics()
    {
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>(o =>
            o.Connectivity = new FakeConnectivityService(isConnected: true));
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IHyperwycDiagnostics>());
    }

    [Fact]
    public void AddHyperwycCore_IHyperwycDiagnostics_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>(o =>
            o.Connectivity = new FakeConnectivityService(isConnected: true));
        using var sp = services.BuildServiceProvider();

        var a = sp.GetRequiredService<IHyperwycDiagnostics>();
        var b = sp.GetRequiredService<IHyperwycDiagnostics>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddHyperwycCore_IHyperwycAndIHyperwycDiagnostics_ShareTheSameInstance()
    {
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>(o =>
            o.Connectivity = new FakeConnectivityService(isConnected: true));
        using var sp = services.BuildServiceProvider();

        var main = sp.GetRequiredService<IHyperwyc>();
        var diagnostics = sp.GetRequiredService<IHyperwycDiagnostics>();

        // Same object, so flushes triggered through IHyperwyc are what IHyperwycDiagnostics
        // reads back. Two separate instances would each hold their own OutboxProcessor and
        // give conflicting answers.
        Assert.Same(main, diagnostics);
    }

    [Fact]
    public async Task IHyperwycDiagnostics_ReflectsWritesTakenByTheHandler()
    {
        // End-to-end sanity: a write queued through the store is visible via the resolved
        // IHyperwycDiagnostics. This is the check that would have caught the DI wiring bug.
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/orders"));

        var services = new ServiceCollection();
        services.AddHyperwycCore(_ => store, o =>
            o.Connectivity = new FakeConnectivityService(isConnected: false));
        using var sp = services.BuildServiceProvider();

        var view = await sp.GetRequiredService<IHyperwycDiagnostics>().GetPendingOutboxAsync();

        var item = Assert.Single(view);
        Assert.Equal("https://example.com/api/orders", item.Url);
    }

    // -------------------------------------------------------------------------
    // Test doubles
    // -------------------------------------------------------------------------

    private sealed class ThrowingTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException("no route to host"));
    }

    /// <summary>
    /// A store that throws on the outbox read, to exercise the StoreHealth guard.
    /// </summary>
    private sealed class ThrowingStore : IHyperwycStore
    {
        public Task<IReadOnlyList<QueuedWrite>> GetPendingOutboxAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<QueuedWrite>>(new InvalidOperationException("store is broken"));

        public Task<CachedResponse?> GetCachedResponseAsync(string url, CancellationToken ct = default) =>
            Task.FromResult<CachedResponse?>(null);
        public Task PutCachedResponseAsync(CachedResponse response, CancellationToken ct = default) => Task.CompletedTask;
        public Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertQueuedWriteAsync(QueuedWrite write, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveDeliveredAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
