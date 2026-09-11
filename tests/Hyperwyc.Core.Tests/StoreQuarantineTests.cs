using System.Net;
using System.Security.Cryptography;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue 62: an unreadable store is set aside once and replaced with a clean one, so
/// caching and queueing carry on rather than being off for the rest of the session.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UnreadableStoreTests"/> covers what happens when that is not possible, which is
/// still the behaviour issue #49 settled on: report and step aside. These are the cases where
/// the store can do something about it.
/// </para>
/// <para>
/// The second failure is the interesting one. It is not bounded by a size or an age — a store
/// that becomes unreadable twice is a systemic fault rather than an incident, so falling back to
/// reporting is right on its own merits, and the bound on accumulation comes free.
/// </para>
/// </remarks>
public class StoreQuarantineTests
{
    private const string Url = "https://example.com/api/products";

    /// <summary>
    /// A store that is broken until it is set aside, and can be set aside once — the shape of
    /// <c>CabinetStore</c>, without the filesystem.
    /// </summary>
    private sealed class QuarantinableStore : IHyperwycStore
    {
        private readonly InMemoryStore _inner = new();

        /// <summary>Fails every operation while true.</summary>
        public bool Broken { get; set; } = true;

        /// <summary>Whether a quarantine has already been used up, as a directory would be.</summary>
        public bool Quarantined { get; private set; }

        public int QuarantineAttempts { get; private set; }

        /// <summary>Models a store whose recovery itself fails — a full or read-only disk.</summary>
        public bool FailToQuarantine { get; set; }

        private void Guard()
        {
            if (Broken) throw new CryptographicException("the key does not match");
        }

        public Task<bool> TryQuarantineAsync(CancellationToken ct = default)
        {
            QuarantineAttempts++;

            if (FailToQuarantine) throw new IOException("no space left on device");
            if (Quarantined) return Task.FromResult(false);

            Quarantined = true;
            Broken = false;
            return Task.FromResult(true);
        }

        public Task<CachedResponse?> GetCachedResponseAsync(string url, CancellationToken ct = default)
        {
            Guard();
            return _inner.GetCachedResponseAsync(url, ct);
        }

        public Task<IReadOnlyList<QueuedWrite>> GetPendingOutboxAsync(CancellationToken ct = default)
        {
            Guard();
            return _inner.GetPendingOutboxAsync(ct);
        }

        public Task PutCachedResponseAsync(CachedResponse response, CancellationToken ct = default)
        {
            Guard();
            return _inner.PutCachedResponseAsync(response, ct);
        }

        public Task UpsertQueuedWriteAsync(QueuedWrite write, CancellationToken ct = default)
        {
            Guard();
            return _inner.UpsertQueuedWriteAsync(write, ct);
        }

        public Task RemoveDeliveredAsync(string id, CancellationToken ct = default)
        {
            Guard();
            return _inner.RemoveDeliveredAsync(id, ct);
        }

        public Task InvalidateCacheForPrefixAsync(string prefix, CancellationToken ct = default)
        {
            Guard();
            return _inner.InvalidateCacheForPrefixAsync(prefix, ct);
        }

        public Task ResetAsync(CancellationToken ct = default)
        {
            // Takes the quarantine with it, as CabinetStore does: reset means discard, and the
            // counter goes with the contents it was counting.
            Quarantined = false;
            return _inner.ResetAsync(ct);
        }
    }

    private static (HyperwycHandler Handler, StoreHealth Health, List<HyperwycEvent> Events)
        Build(IHyperwycStore store, HttpMessageHandler inner, bool connected = true)
    {
        var events = new HyperwycEventStream();
        var collected = new List<HyperwycEvent>();
        events.Subscribe(new DelegateObserver<HyperwycEvent>(collected.Add));

        var options = new HyperwycOptions();
        var health = new StoreHealth(store, options, events);
        var handler = new HyperwycHandler(
            store, new FakeConnectivityService(connected), events, options, health)
        { InnerHandler = inner };

        return (handler, health, collected);
    }

    private static StubHttpMessageHandler Ok(string body = "from-network") =>
        new((Func<HttpRequestMessage, HttpResponseMessage>)(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));

    // -------------------------------------------------------------------------
    // The session carries on
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AFailure_SetsTheStoreAside_RatherThanDisablingTheSession()
    {
        var store = new QuarantinableStore();
        var (handler, health, events) = Build(store, Ok());
        using var client = new HttpClient(handler);

        await client.GetAsync(Url);

        Assert.True(health.IsUsable);
        Assert.Contains(events, e => e.Type == HyperwycEventType.OnStoreQuarantined);
        Assert.DoesNotContain(events, e => e.Type == HyperwycEventType.OnStoreUnreadable);
    }

    [Fact]
    public async Task AfterTheStoreIsSetAside_ReadsAreCachedAgain()
    {
        // The acceptance criterion, end to end: the first request pays for the broken store and
        // every request after it behaves as though the device had simply never held one.
        var store = new QuarantinableStore();
        var (handler, _, _) = Build(store, Ok());
        using var client = new HttpClient(handler);

        await client.GetAsync(Url);   // trips the failure, and recovers
        await client.GetAsync(Url);   // populates the clean store

        Assert.NotNull(await store.GetCachedResponseAsync(Url));
    }

    [Fact]
    public async Task AfterTheStoreIsSetAside_OfflineWritesAreQueuedAgain()
    {
        var store = new QuarantinableStore();
        var transport = new StubHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("offline")));
        var (handler, _, _) = Build(store, transport, connected: false);
        using var client = new HttpClient(handler);

        // The first write is declined — Hyperwyc will not answer 202 for a write it did not
        // manage to store, and the request fails as it would without Hyperwyc installed.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync(Url, new StringContent("{}")));

        var second = await client.PostAsync(Url, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Single(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task TheQuarantine_IsPublishedOnce_NotOncePerRequest()
    {
        var store = new QuarantinableStore();
        var (handler, _, events) = Build(store, Ok());
        using var client = new HttpClient(handler);

        for (var i = 0; i < 5; i++) await client.GetAsync(Url);

        Assert.Single(events, e => e.Type == HyperwycEventType.OnStoreQuarantined);
    }

    // -------------------------------------------------------------------------
    // Once, and only once
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ASecondFailure_IsReportedRatherThanQuarantined()
    {
        var store = new QuarantinableStore();
        var (handler, health, events) = Build(store, Ok());
        using var client = new HttpClient(handler);

        await client.GetAsync(Url);
        Assert.True(health.IsUsable);

        // The same store breaks again. That is systemic rather than incidental, and churning a
        // second copy onto the device would hide exactly the thing someone needs to see.
        store.Broken = true;
        await client.GetAsync(Url);

        Assert.False(health.IsUsable);
        Assert.Single(events, e => e.Type == HyperwycEventType.OnStoreUnreadable);
        Assert.Equal(1, store.QuarantineAttempts);
    }

    [Fact]
    public async Task AStoreThatCannotBeSetAside_BehavesExactlyAsItDidBefore()
    {
        // A store with nowhere to put its contents says so by returning false, which is what the
        // default interface implementation does — so every existing IHyperwycStore keeps the
        // issue #49 behaviour without being changed.
        var store = new QuarantinableStore { FailToQuarantine = true };
        var (handler, health, events) = Build(store, Ok());
        using var client = new HttpClient(handler);

        var response = await client.GetAsync(Url);

        // Above all, the failed recovery does not escape into the caller's HTTP call.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(health.IsUsable);
        Assert.Single(events, e => e.Type == HyperwycEventType.OnStoreUnreadable);
    }

    // -------------------------------------------------------------------------
    // A failure against contents that are already gone
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AStaleReport_DoesNotDisableTheSession()
    {
        // The common case, not an exotic one: two requests in flight both hit the broken store
        // and both throw. The first sets it aside and recovers; the second is reporting a
        // failure that is already history, and must not latch the session off behind it.
        var store = new QuarantinableStore();
        var events = new HyperwycEventStream();
        var collected = new List<HyperwycEvent>();
        events.Subscribe(new DelegateObserver<HyperwycEvent>(collected.Add));
        var health = new StoreHealth(store, new HyperwycOptions(), events);

        var generation = health.Generation;

        await health.ReportUnreadableAsync(new CryptographicException(), generation);
        Assert.True(health.IsUsable);

        // The second request's catch block, running after the first has recovered, still holding
        // the generation it read before it called the store.
        await health.ReportUnreadableAsync(new CryptographicException(), generation);

        Assert.True(health.IsUsable);
        Assert.DoesNotContain(collected, e => e.Type == HyperwycEventType.OnStoreUnreadable);
    }

    [Fact]
    public async Task ConcurrentFailures_SetTheStoreAsideOnce_AndLeaveItUsable()
    {
        var store = new QuarantinableStore();
        var (handler, health, events) = Build(store, Ok());
        using var client = new HttpClient(handler);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync(Url)));

        Assert.True(health.IsUsable);
        Assert.Single(events, e => e.Type == HyperwycEventType.OnStoreQuarantined);
        Assert.DoesNotContain(events, e => e.Type == HyperwycEventType.OnStoreUnreadable);
    }

    [Fact]
    public async Task AFreshFailureAfterAQuarantine_IsNotMistakenForAStaleOne()
    {
        // The other half of the generation check: a report carrying the current generation is
        // about the store as it is now, and has to be acted on.
        var store = new QuarantinableStore();
        var health = new StoreHealth(store, new HyperwycOptions(), new HyperwycEventStream());

        await health.ReportUnreadableAsync(new CryptographicException(), health.Generation);
        Assert.True(health.IsUsable);

        await health.ReportUnreadableAsync(new CryptographicException(), health.Generation);
        Assert.False(health.IsUsable);
    }

    // -------------------------------------------------------------------------
    // Reset re-arms it
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AfterAReset_TheStoreMayBeSetAsideAgain()
    {
        // An explicit reset discards the previous quarantine along with everything else, so the
        // counter that bounds accumulation has gone with it.
        var store = new QuarantinableStore();
        var health = new StoreHealth(store, new HyperwycOptions(), new HyperwycEventStream());

        await health.ReportUnreadableAsync(new CryptographicException(), health.Generation);
        await health.ReportUnreadableAsync(new CryptographicException(), health.Generation);
        Assert.False(health.IsUsable);

        // What IHyperwyc.ResetStoreAsync does: clear the store, then tell health about it.
        await store.ResetAsync();
        health.Recovered();

        store.Broken = true;
        await health.ReportUnreadableAsync(new CryptographicException(), health.Generation);

        Assert.True(health.IsUsable);

        // Two attempts, not three: the second failure never asked, because a store already set
        // aside this session is not set aside again. The reset is what re-arms it.
        Assert.Equal(2, store.QuarantineAttempts);
    }

    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
