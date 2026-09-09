using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue #40: when a queued write is eventually delivered, the application can see which
/// write it was and what the server said — whatever it said.
/// </summary>
/// <remarks>
/// Since ADR 0010 the event is the <em>only</em> report of a delivery: the write is discarded
/// along with what came back, because an ordinary HTTP response is the application's to keep.
/// The one outcome still written to the store is a transport failure, where the write is
/// still there to carry it.
/// </remarks>
public class DeferredOutcomeTests
{
    private const string Url = "https://example.com/api/sales";

    // -------------------------------------------------------------------------
    // Correlation — which queued write is this?
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CallerSuppliedCorrelationId_IsUsedAsTheCorrelationId()
    {
        var store = new InMemoryStore();
        using var sp = BuildOfflineClient(store, out var factory);

        using var request = PostTo(Url, correlationId: "sale-42");
        await factory.CreateClient("TestApi").SendAsync(request);

        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal("sale-42", queued.CorrelationId);

        // The write's id stays Hyperwyc's own: the caller's value carries no uniqueness
        // guarantee and must never become a store key.
        Assert.NotEqual("sale-42", queued.Id);
    }

    [Fact]
    public async Task NoCorrelationId_OneIsGenerated()
    {
        var store = new InMemoryStore();
        using var sp = BuildOfflineClient(store, out var factory);

        await factory.CreateClient("TestApi").PostAsync(Url, new StringContent("{}"));

        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.False(string.IsNullOrWhiteSpace(queued.CorrelationId));
        Assert.Equal(queued.Id, queued.CorrelationId);
    }

    [Theory]
    [InlineData("sale-42")]
    [InlineData(null)]
    public async Task QueuedResponse_ReturnsTheCorrelationId(string? supplied)
    {
        var store = new InMemoryStore();
        using var sp = BuildOfflineClient(store, out var factory);

        using var request = PostTo(Url, supplied);
        var response = await factory.CreateClient("TestApi").SendAsync(request);

        // Always present, whether the caller supplied the value or Hyperwyc minted it — the
        // caller who planned ahead has it confirmed, the one who didn't now has it.
        var header = Assert.Single(response.Headers.GetValues("X-Hyperwyc-Correlation-Id"));
        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal(queued.CorrelationId, header);
        if (supplied is not null)
            Assert.Equal(supplied, header);
    }

    [Fact]
    public async Task QueuedEvent_CarriesCorrelationRequestIdAndBody()
    {
        var store = new InMemoryStore();
        using var sp = BuildOfflineClient(store, out var factory);
        var events = Collect(sp);

        using var request = PostTo(Url, "sale-42", body: """{"quantity":60}""");
        await factory.CreateClient("TestApi").SendAsync(request);

        var queued = Assert.Single(events, e => e.Type == HyperwycEventType.OnQueued);
        Assert.Equal("sale-42", queued.CorrelationId);
        Assert.Equal("""{"quantity":60}""", queued.GetRequestBodyAsText());
        Assert.NotNull(queued.RequestId);
        Assert.Null(queued.Outcome);
    }

    [Fact]
    public async Task SeveralWritesToTheSameUrl_EachFailureIdentifiesItsOwn()
    {
        // The sharpest gap in the issue: URL and method alone cannot tell three queued sales
        // apart, so a per-sale "failed" badge is unimplementable without this.
        var store = new InMemoryStore();
        foreach (var id in new[] { "sale-1", "sale-2", "sale-3" })
            await store.UpsertQueuedWriteAsync(Outbox(correlationId: id, body: id));

        var events = new List<HyperwycEvent>();
        var stream = new HyperwycEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        // Only sale-2 is rejected; the others succeed.
        var transport = new StubHttpMessageHandler(async request =>
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(
                body == "sale-2" ? HttpStatusCode.Conflict : HttpStatusCode.OK);
        });

        await using var orchestrator = Orchestrator(store, transport, stream);
        await orchestrator.FlushAsync();

        // All three raise the same event; the correlation id and the status are what tell them
        // apart, which is the whole point of issue 40.
        var rejected = Assert.Single(
            events, e => e.Type == HyperwycEventType.OnDelivered && e.Outcome?.StatusCode == 409);
        Assert.Equal("sale-2", rejected.CorrelationId);
        Assert.Equal(3, events.Count(e => e.Type == HyperwycEventType.OnDelivered));
    }

    // -------------------------------------------------------------------------
    // What the server said
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Rejected_SurfacesStatusReasonAndBody()
    {
        const string ServerSaid = """{"error":"Only 20 left in stock.","available":20}""";

        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Outbox("sale-42"));

        var events = new List<HyperwycEvent>();
        var stream = new HyperwycEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                ReasonPhrase = "Conflict",
                Content = new StringContent(ServerSaid, Encoding.UTF8, "application/json"),
            });

        await using var orchestrator = Orchestrator(store, transport, stream);
        await orchestrator.FlushAsync();

        var delivered = Assert.Single(events, e => e.Type == HyperwycEventType.OnDelivered);
        var outcome = Assert.IsType<DeliveryOutcome>(delivered.Outcome);

        Assert.Equal(DeliveryOutcomeKind.Delivered, outcome.Kind);
        Assert.Equal(409, outcome.StatusCode);
        Assert.Equal("Conflict", outcome.ReasonPhrase);
        Assert.Equal(ServerSaid, outcome.GetBodyAsText());
        Assert.False(outcome.BodyTruncated);
    }

    [Fact]
    public async Task Delivered_CarriesTheServerResponse()
    {
        const string Created = """{"id":"srv-77","recordedAt":"2026-08-14T09:12:33Z"}""";

        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Outbox("sale-42"));

        var events = new List<HyperwycEvent>();
        var stream = new HyperwycEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(Created, Encoding.UTF8, "application/json"),
            });

        await using var orchestrator = Orchestrator(store, transport, stream);
        await orchestrator.FlushAsync();

        // The caller never saw this response, so without it they cannot reconcile their local
        // record against what the server actually stored.
        var synced = Assert.Single(events, e => e.Type == HyperwycEventType.OnDelivered);
        var outcome = Assert.IsType<DeliveryOutcome>(synced.Outcome);
        Assert.Equal(DeliveryOutcomeKind.Delivered, outcome.Kind);
        Assert.Equal(201, outcome.StatusCode);
        Assert.Equal(Created, outcome.GetBodyAsText());
    }

    [Fact]
    public async Task TransportFailure_IsDistinguishableFromAnHttpError()
    {
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Outbox("sale-42"));

        var transport = new StubHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(
                _ => throw new HttpRequestException("No such host is known.")));

        await using var orchestrator = Orchestrator(store, transport, new HyperwycEventStream());
        await orchestrator.FlushAsync();

        // No event: the flush abandons and the write keeps its place. The record is what
        // explains an outbox that will not drain.
        var pending = Assert.Single(await store.GetPendingOutboxAsync());
        var outcome = Assert.IsType<DeliveryOutcome>(pending.LastOutcome);

        Assert.Equal(DeliveryOutcomeKind.TransportFailure, outcome.Kind);
        Assert.Null(outcome.StatusCode);
        Assert.Contains("No such host", outcome.Error);
    }

    [Fact]
    public async Task ServerErrorAndRejection_AreBothFinal_AndCarryTheirOwnStatus()
    {
        // Both are answers, so both are final: the request reached the API either way. What a
        // consumer needs is not a retry distinction Hyperwyc cannot honour, but the status the
        // server actually gave, so the application can decide what to do about it.
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Outbox("refused", body: "refused"));
        await store.UpsertQueuedWriteAsync(Outbox("unwell", body: "unwell"));

        var events = new List<HyperwycEvent>();
        var stream = new HyperwycEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        var transport = new StubHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(body == "refused"
                ? HttpStatusCode.Conflict
                : HttpStatusCode.ServiceUnavailable);
        });

        await using var orchestrator = Orchestrator(store, transport, stream);
        await orchestrator.FlushAsync();

        var delivered = events.Where(e => e.Type == HyperwycEventType.OnDelivered).ToList();
        Assert.Equal(2, delivered.Count);

        var refused = Assert.Single(delivered, e => e.CorrelationId == "refused");
        Assert.Equal(DeliveryOutcomeKind.Delivered, refused.Outcome?.Kind);
        Assert.Equal(409, refused.Outcome?.StatusCode);

        var unwell = Assert.Single(delivered, e => e.CorrelationId == "unwell");
        Assert.Equal(DeliveryOutcomeKind.Delivered, unwell.Outcome?.Kind);
        Assert.Equal(503, unwell.Outcome?.StatusCode);

        // Neither is left in the outbox waiting for a flush that may never come.
        Assert.Empty(await store.GetPendingOutboxAsync());
    }


    // -------------------------------------------------------------------------
    // Body capture limits
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OversizedBody_IsClippedAndFlagged()
    {
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Outbox("sale-42"));

        var events = new List<HyperwycEvent>();
        var stream = new HyperwycEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(new string('x', 5000)),
            });

        await using var orchestrator = Orchestrator(
            store, transport, stream, options: new HyperwycOptions
            {
                MaxOutcomeBodyBytes = 100,
            });
        await orchestrator.FlushAsync();

        var outcome = Assert.IsType<DeliveryOutcome>(
            Assert.Single(events, e => e.Type == HyperwycEventType.OnDelivered).Outcome);

        // Clipped rather than dropped: half an error message is still actionable.
        Assert.True(outcome.BodyTruncated);
        Assert.Equal(100, outcome.Body!.Length);
    }

    [Fact]
    public async Task ZeroCap_CapturesNoBodyButStillReportsTheStatus()
    {
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Outbox("sale-42"));

        var events = new List<HyperwycEvent>();
        var stream = new HyperwycEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("something went wrong"),
            });

        await using var orchestrator = Orchestrator(
            store, transport, stream, options: new HyperwycOptions
            {
                MaxOutcomeBodyBytes = 0,
            });
        await orchestrator.FlushAsync();

        var outcome = Assert.IsType<DeliveryOutcome>(
            Assert.Single(events, e => e.Type == HyperwycEventType.OnDelivered).Outcome);

        Assert.Null(outcome.Body);
        Assert.False(outcome.BodyTruncated);
        Assert.Equal(400, outcome.StatusCode);
    }

    // -------------------------------------------------------------------------
    // The record that does have to outlive the process
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TransportFailure_CarriesItsOutcomeThroughSerialisation()
    {
        var captured = new OutcomeCapturingStore(new InMemoryStore());
        await captured.UpsertQueuedWriteAsync(Outbox("sale-42"));

        var transport = new StubHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(
                _ => throw new HttpRequestException("No such host is known.")));

        await using var orchestrator = Orchestrator(captured, transport, new HyperwycEventStream());
        await orchestrator.FlushAsync();

        // Round-tripped through JSON as a durable store would, which is the real test of the
        // decision to keep exceptions out of the record: an Exception would not survive this.
        // This is the only outcome still written down — a delivery leaves nothing behind, and
        // that is what the event is for. See ADR 0010.
        var restored = captured.LastSnapshotAsRestored();
        var outcome = Assert.IsType<DeliveryOutcome>(restored.LastOutcome);

        Assert.Equal("sale-42", restored.CorrelationId);
        Assert.Equal(DeliveryOutcomeKind.TransportFailure, outcome.Kind);
        Assert.Null(outcome.StatusCode);
        Assert.Contains("No such host", outcome.Error);
    }

    [Fact]
    public async Task DeliveredWrite_IsNotWrittenBackToTheStore()
    {
        // The counterpart, and the change ADR 0010 actually makes: a rejection used to be
        // upserted with its outcome and then flagged. Now nothing is written at all.
        var captured = new OutcomeCapturingStore(new InMemoryStore());
        await captured.UpsertQueuedWriteAsync(Outbox("sale-42"));
        captured.ForgetSnapshot();

        var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"error":"Only 20 left in stock."}"""),
            });

        await using var orchestrator = Orchestrator(captured, transport, new HyperwycEventStream());
        await orchestrator.FlushAsync();

        Assert.Null(captured.LastSnapshot);
        Assert.Empty(await captured.GetPendingOutboxAsync());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static QueuedWrite Outbox(string correlationId, string? body = null) =>
        new()
        {
            Url = Url,
            Method = "POST",
            CorrelationId = correlationId,
            RequestBody = body is null ? null : System.Text.Encoding.UTF8.GetBytes(body),
        };

    private static HttpRequestMessage PostTo(string url, string? correlationId, string body = "{}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body),
        };
        if (correlationId is not null)
            request.Options.Set(HyperwycRequestOptions.CorrelationId, correlationId);
        return request;
    }

    private static OutboxProcessor Orchestrator(
        IHyperwycStore store,
        StubHttpMessageHandler transport,
        HyperwycEventStream events,
        HyperwycOptions? options = null) =>
        new(store,
            new FakeConnectivityService(isConnected: true),
            events,
            options ?? new HyperwycOptions(),
            transport, TestHealth());

    private static ServiceProvider BuildOfflineClient(
        InMemoryStore store, out IHttpClientFactory factory)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService>(new FakeConnectivityService(isConnected: false));
        services.AddHttpClient("TestApi")
            .AddHyperwycHandler()
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(
                new HttpResponseMessage(HttpStatusCode.OK)));
        services.AddHyperwycCore(_ => store, o => o.FlushOnStartup = false);

        var sp = services.BuildServiceProvider();
        factory = sp.GetRequiredService<IHttpClientFactory>();
        return sp;
    }

    private static List<HyperwycEvent> Collect(IServiceProvider sp)
    {
        var events = new List<HyperwycEvent>();
        sp.GetRequiredService<IHyperwyc>().Events.Subscribe(new Collector(events));
        return events;
    }

    private sealed class Collector(List<HyperwycEvent> collected) : IObserver<HyperwycEvent>
    {
        public void OnNext(HyperwycEvent value) => collected.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>
    /// Delegates to an <see cref="InMemoryStore"/> while snapshotting each queued-write upsert
    /// as JSON, so a test can read back what a durable store would actually have written rather
    /// than the same object it handed in.
    /// </summary>
    private sealed class OutcomeCapturingStore(InMemoryStore inner) : IHyperwycStore
    {
        private string? _lastSnapshot;

        public string? LastSnapshot => _lastSnapshot;

        public void ForgetSnapshot() => _lastSnapshot = null;

        public QueuedWrite LastSnapshotAsRestored() =>
            JsonSerializer.Deserialize<QueuedWrite>(_lastSnapshot!)!;

        public Task UpsertQueuedWriteAsync(QueuedWrite write, CancellationToken ct = default)
        {
            _lastSnapshot = JsonSerializer.Serialize(write);
            return inner.UpsertQueuedWriteAsync(write, ct);
        }

        public Task PutCachedResponseAsync(CachedResponse response, CancellationToken ct = default) =>
            inner.PutCachedResponseAsync(response, ct);

        public Task<CachedResponse?> GetCachedResponseAsync(string url, CancellationToken ct = default) =>
            inner.GetCachedResponseAsync(url, ct);

        public Task<IReadOnlyList<QueuedWrite>> GetPendingOutboxAsync(CancellationToken ct = default) =>
            inner.GetPendingOutboxAsync(ct);

        public Task RemoveDeliveredAsync(string id, CancellationToken ct = default) =>
            inner.RemoveDeliveredAsync(id, ct);

        public Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default) =>
            inner.InvalidateCacheForPrefixAsync(urlPrefix, ct);

        public Task ResetAsync(CancellationToken ct = default) => inner.ResetAsync(ct);
    }
}
