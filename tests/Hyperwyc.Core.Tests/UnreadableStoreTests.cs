using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue #49: a store Hyperwyc cannot read is reported and stepped around, not repaired,
/// destroyed, or allowed to escape into the caller's HTTP call.
/// </summary>
public class UnreadableStoreTests
{
    private const string Url = "https://example.com/api/products";

    /// <summary>
    /// Fails every read the way a real store does — a wrong key throws
    /// <see cref="CryptographicException"/>, a changed persisted shape throws
    /// <see cref="JsonException"/>. Writes are allowed through so a test can show that
    /// Hyperwyc still declines to queue.
    /// </summary>
    private sealed class UnreadableStore(Exception failure) : IHyperwycStore
    {
        private readonly InMemoryStore _inner = new();
        public int WritesAccepted { get; private set; }

        public Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default) =>
            throw failure;

        public Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default) =>
            throw failure;

        public Task UpsertAsync(Envelope envelope, CancellationToken ct = default)
        {
            WritesAccepted++;
            return _inner.UpsertAsync(envelope, ct);
        }

        public Task MarkDeliveredAsync(string id, CancellationToken ct = default) =>
            _inner.MarkDeliveredAsync(id, ct);

        public Task MoveToDeadLetterAsync(string id, CancellationToken ct = default) =>
            _inner.MoveToDeadLetterAsync(id, ct);

        public Task InvalidateCacheForPrefixAsync(string prefix, CancellationToken ct = default) =>
            _inner.InvalidateCacheForPrefixAsync(prefix, ct);

        public Task ResetAsync(CancellationToken ct = default) => _inner.ResetAsync(ct);
    }

    private static (HyperwycHandler Handler, StoreHealth Health, List<HyperwycEvent> Events)
        Build(IHyperwycStore store, HttpMessageHandler inner, bool connected)
    {
        var events = new HyperwycEventStream();
        var collected = new List<HyperwycEvent>();
        events.Subscribe(new Collector(collected));

        var health = new StoreHealth(events);
        var handler = new HyperwycHandler(
            store, new FakeConnectivityService(connected), events, new HyperwycOptions(), health)
        { InnerHandler = inner };

        return (handler, health, collected);
    }

    public static TheoryData<Exception> StoreFailures() =>
    [
        new CryptographicException("the key does not match"),
        new JsonException("could not be converted to System.Byte[]"),
        new IOException("the file is gone"),
    ];

    // -------------------------------------------------------------------------
    // Nothing escapes into the caller's request
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(StoreFailures))]
    public async Task AReadFailure_DoesNotEscapeIntoTheCallersRequest(Exception failure)
    {
        // The defect this exists for: the exception surfaced out of GetFromJsonAsync, so it read
        // as an HTTP bug and the store was never suspected.
        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var (handler, _, _) = Build(new UnreadableStore(failure), transport, connected: true);
        using var client = new HttpClient(handler);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AReadFailure_FallsThroughToTheNetwork()
    {
        var transport = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("from-network") });
        var (handler, _, _) = Build(
            new UnreadableStore(new CryptographicException()), transport, connected: true);
        using var client = new HttpClient(handler);

        Assert.Equal("from-network", await (await client.GetAsync(Url)).Content.ReadAsStringAsync());
    }

    // -------------------------------------------------------------------------
    // Reported once, and only once
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TheFailure_IsPublishedAsAnEvent()
    {
        var (handler, _, events) = Build(
            new UnreadableStore(new CryptographicException()),
            new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            connected: true);
        using var client = new HttpClient(handler);

        await client.GetAsync(Url);

        Assert.Single(events, e => e.Type == HyperwycEventType.OnStoreUnreadable);
    }

    [Fact]
    public async Task TheFailure_IsReportedOncePerStore_NotOncePerRequest()
    {
        var (handler, _, events) = Build(
            new UnreadableStore(new CryptographicException()),
            new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)),
            connected: true);
        using var client = new HttpClient(handler);

        for (var i = 0; i < 5; i++) await client.GetAsync(Url);

        Assert.Single(events, e => e.Type == HyperwycEventType.OnStoreUnreadable);
    }

    // -------------------------------------------------------------------------
    // Custody is declined, not faked
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AnOfflineWrite_IsNotAcceptedWithA202OnceTheStoreIsUnreadable()
    {
        var store = new UnreadableStore(new CryptographicException());
        var transport = new StubHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("offline")));
        var (handler, health, _) = Build(store, transport, connected: false);
        using var client = new HttpClient(handler);

        // The read that trips the latch.
        health.ReportUnreadable(new CryptographicException(), usingDerivedKey: true);

        // A 202 would promise delivery Hyperwyc has no way to keep, so the write passes through
        // and fails as it would without Hyperwyc installed.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync(Url, new StringContent("{}")));

        Assert.Equal(0, store.WritesAccepted);
    }

    [Fact]
    public async Task AnOfflineWrite_WhoseStoreFailsOnTheWayIn_IsNotAcceptedEither()
    {
        // The store accepts nothing at all — the write must not come back 202 regardless of
        // which operation failed first.
        var store = new AlwaysFailingStore();
        var transport = new StubHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("offline")));
        var (handler, _, _) = Build(store, transport, connected: false);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync(Url, new StringContent("{}")));
    }

    private sealed class AlwaysFailingStore : IHyperwycStore
    {
        private static Exception Fail() => new CryptographicException("unreadable");
        public Task<Envelope?> GetCachedResponseAsync(string u, CancellationToken ct = default) => throw Fail();
        public Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default) => throw Fail();
        public Task UpsertAsync(Envelope e, CancellationToken ct = default) => throw Fail();
        public Task MarkDeliveredAsync(string id, CancellationToken ct = default) => throw Fail();
        public Task MoveToDeadLetterAsync(string id, CancellationToken ct = default) => throw Fail();
        public Task InvalidateCacheForPrefixAsync(string p, CancellationToken ct = default) => throw Fail();
        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Nothing is destroyed, and cancellation is not swallowed
    // -------------------------------------------------------------------------

    [Fact]
    public void Cancellation_IsNotTreatedAsAStoreFailure()
    {
        // Catching everything would swallow the caller giving up, which is not a store problem.
        Assert.False(StoreHealth.IsStoreFailure(new OperationCanceledException()));
        Assert.True(StoreHealth.IsStoreFailure(new CryptographicException()));
        Assert.True(StoreHealth.IsStoreFailure(new JsonException()));
    }

    [Fact]
    public void Recovered_ClearsTheLatchSoTheStoreIsUsedAgain()
    {
        var health = new StoreHealth(new HyperwycEventStream());

        health.ReportUnreadable(new CryptographicException(), usingDerivedKey: false);
        Assert.False(health.IsUsable);

        health.Recovered();
        Assert.True(health.IsUsable);
    }

    private sealed class Collector(List<HyperwycEvent> collected) : IObserver<HyperwycEvent>
    {
        public void OnNext(HyperwycEvent value) => collected.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
