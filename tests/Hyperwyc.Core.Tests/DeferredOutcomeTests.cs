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
/// Covers issue #40: when a queued write is eventually delivered or rejected, the application
/// can see which write it was and what the server said.
/// </summary>
/// <remarks>
/// The persisted record is the primary artefact here and the event is a view of it, so these
/// assert against both — an outcome that only reaches a live subscriber is one a backgrounded
/// mobile app never hears about.
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
        var store = new InMemorySyncStore();
        using var sp = BuildOfflineClient(store, out var factory);

        using var request = PostTo(Url, correlationId: "sale-42");
        await factory.CreateClient("TestApi").SendAsync(request);

        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal("sale-42", queued.CorrelationId);

        // The envelope id stays Hyperwyc's own: the caller's value carries no uniqueness
        // guarantee and must never become a store key.
        Assert.NotEqual("sale-42", queued.Id);
    }

    [Fact]
    public async Task NoCorrelationId_OneIsGenerated()
    {
        var store = new InMemorySyncStore();
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
        var store = new InMemorySyncStore();
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
        var store = new InMemorySyncStore();
        using var sp = BuildOfflineClient(store, out var factory);
        var events = Collect(sp);

        using var request = PostTo(Url, "sale-42", body: """{"quantity":60}""");
        await factory.CreateClient("TestApi").SendAsync(request);

        var queued = Assert.Single(events, e => e.Type == SyncEventType.OnQueued);
        Assert.Equal("sale-42", queued.CorrelationId);
        Assert.Equal("""{"quantity":60}""", queued.RequestBody);
        Assert.NotNull(queued.RequestId);
        Assert.Null(queued.Outcome);
    }

    [Fact]
    public async Task SeveralWritesToTheSameUrl_EachFailureIdentifiesItsOwn()
    {
        // The sharpest gap in the issue: URL and method alone cannot tell three queued sales
        // apart, so a per-sale "failed" badge is unimplementable without this.
        var store = new InMemorySyncStore();
        foreach (var id in new[] { "sale-1", "sale-2", "sale-3" })
            await store.UpsertAsync(Outbox(correlationId: id, body: id));

        var events = new List<SyncEvent>();
        var stream = new SyncEventStream();
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

        var failed = Assert.Single(events, e => e.Type == SyncEventType.OnFailed);
        Assert.Equal("sale-2", failed.CorrelationId);
        Assert.Equal(2, events.Count(e => e.Type == SyncEventType.OnSynced));
    }

    // -------------------------------------------------------------------------
    // What the server said
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Rejected_SurfacesStatusReasonAndBody()
    {
        const string ServerSaid = """{"error":"Only 20 left in stock.","available":20}""";

        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox("sale-42"));

        var events = new List<SyncEvent>();
        var stream = new SyncEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                ReasonPhrase = "Conflict",
                Content = new StringContent(ServerSaid, Encoding.UTF8, "application/json"),
            });

        await using var orchestrator = Orchestrator(store, transport, stream);
        await orchestrator.FlushAsync();

        var failed = Assert.Single(events, e => e.Type == SyncEventType.OnFailed);
        var outcome = Assert.IsType<SyncOutcome>(failed.Outcome);

        Assert.Equal(SyncOutcomeKind.Rejected, outcome.Kind);
        Assert.True(outcome.IsFinal);
        Assert.Equal(409, outcome.StatusCode);
        Assert.Equal("Conflict", outcome.ReasonPhrase);
        Assert.Equal(ServerSaid, outcome.GetBodyAsText());
        Assert.False(outcome.BodyTruncated);
        Assert.Contains("Content-Type", outcome.Headers.Keys);
    }

    [Fact]
    public async Task Synced_CarriesTheServerResponse()
    {
        const string Created = """{"id":"srv-77","recordedAt":"2026-08-14T09:12:33Z"}""";

        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox("sale-42"));

        var events = new List<SyncEvent>();
        var stream = new SyncEventStream();
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
        var synced = Assert.Single(events, e => e.Type == SyncEventType.OnSynced);
        var outcome = Assert.IsType<SyncOutcome>(synced.Outcome);
        Assert.Equal(SyncOutcomeKind.Succeeded, outcome.Kind);
        Assert.Equal(201, outcome.StatusCode);
        Assert.Equal(Created, outcome.GetBodyAsText());
    }

    [Fact]
    public async Task TransportFailure_IsDistinguishableFromAnHttpError()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox("sale-42"));

        var transport = new StubHttpMessageHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(
                _ => throw new HttpRequestException("No such host is known.")));

        await using var orchestrator = Orchestrator(store, transport, new SyncEventStream());
        await orchestrator.FlushAsync();

        // No event: the flush abandons and the envelope keeps its place. The record is what
        // explains an outbox that will not drain.
        var pending = Assert.Single(await store.GetPendingOutboxAsync());
        var outcome = Assert.IsType<SyncOutcome>(pending.LastOutcome);

        Assert.Equal(SyncOutcomeKind.TransportFailure, outcome.Kind);
        Assert.False(outcome.IsFinal);
        Assert.Null(outcome.StatusCode);
        Assert.Contains("No such host", outcome.Error);

        // The network being unusable says nothing about the request, so it costs no budget.
        Assert.Equal(0, pending.RetryCount);
        Assert.Equal(0, outcome.AttemptCount);
    }

    [Fact]
    public async Task BudgetExhausted_IsDistinguishableFromRejection()
    {
        var store = new InMemorySyncStore();
        var captured = new OutcomeCapturingStore(store);
        await captured.UpsertAsync(Outbox("sale-42"));

        var transport = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var policy = new FakeSyncPolicy(retryOptions:
            new RetryOptions(MaxRetries: 1, InitialDelay: TimeSpan.Zero, BackoffMultiplier: 1.0));

        var events = new List<SyncEvent>();
        var stream = new SyncEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        await using var orchestrator = Orchestrator(captured, transport, stream, policy);
        await orchestrator.FlushAsync();   // attempt 1 — deferred
        await orchestrator.FlushAsync();   // attempt 2 — budget spent, dead-lettered

        var failed = Assert.Single(events, e => e.Type == SyncEventType.OnFailed);
        var outcome = Assert.IsType<SyncOutcome>(failed.Outcome);

        // Not Rejected: the server never refused it, Hyperwyc gave up. That is the difference
        // between "this will never work" and "worth offering a retry".
        Assert.Equal(SyncOutcomeKind.TransientFailure, outcome.Kind);
        Assert.True(outcome.IsFinal);
        Assert.Equal(503, outcome.StatusCode);
    }

    [Fact]
    public async Task Retrying_CarriesThePreviousAttemptsOutcome()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox("sale-42"));

        var transport = new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var policy = new FakeSyncPolicy(retryOptions:
            new RetryOptions(MaxRetries: 5, InitialDelay: TimeSpan.Zero, BackoffMultiplier: 1.0));

        var events = new List<SyncEvent>();
        var stream = new SyncEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        await using var orchestrator = Orchestrator(store, transport, stream, policy);
        await orchestrator.FlushAsync();
        await orchestrator.FlushAsync();

        var retrying = Assert.Single(events, e => e.Type == SyncEventType.OnRetrying);
        var outcome = Assert.IsType<SyncOutcome>(retrying.Outcome);

        // Explains why the retry is happening, which is what a diagnostics log wants.
        Assert.Equal(SyncOutcomeKind.TransientFailure, outcome.Kind);
        Assert.False(outcome.IsFinal);
        Assert.Equal(1, outcome.AttemptCount);
        Assert.Equal("sale-42", retrying.CorrelationId);
    }

    // -------------------------------------------------------------------------
    // Body capture limits
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OversizedBody_IsClippedAndFlagged()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox("sale-42"));

        var events = new List<SyncEvent>();
        var stream = new SyncEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(new string('x', 5000)),
            });

        await using var orchestrator = Orchestrator(
            store, transport, stream, options: new HyperwycOptions
            {
                ConnectivityDebounceDelay = TimeSpan.Zero,
                MaxOutcomeBodyBytes = 100,
            });
        await orchestrator.FlushAsync();

        var outcome = Assert.IsType<SyncOutcome>(
            Assert.Single(events, e => e.Type == SyncEventType.OnFailed).Outcome);

        // Clipped rather than dropped: half an error message is still actionable.
        Assert.True(outcome.BodyTruncated);
        Assert.Equal(100, outcome.Body!.Length);
    }

    [Fact]
    public async Task ZeroCap_CapturesNoBodyButStillReportsTheStatus()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(Outbox("sale-42"));

        var events = new List<SyncEvent>();
        var stream = new SyncEventStream();
        using var subscription = stream.Subscribe(new Collector(events));

        var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("something went wrong"),
            });

        await using var orchestrator = Orchestrator(
            store, transport, stream, options: new HyperwycOptions
            {
                ConnectivityDebounceDelay = TimeSpan.Zero,
                MaxOutcomeBodyBytes = 0,
            });
        await orchestrator.FlushAsync();

        var outcome = Assert.IsType<SyncOutcome>(
            Assert.Single(events, e => e.Type == SyncEventType.OnFailed).Outcome);

        Assert.Null(outcome.Body);
        Assert.False(outcome.BodyTruncated);
        Assert.Equal(400, outcome.StatusCode);
    }

    // -------------------------------------------------------------------------
    // The record has to outlive the process
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DeadLetteredEnvelope_CarriesItsOutcomeThroughSerialisation()
    {
        var captured = new OutcomeCapturingStore(new InMemorySyncStore());
        await captured.UpsertAsync(Outbox("sale-42"));

        var transport = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"error":"Only 20 left in stock."}"""),
            });

        await using var orchestrator = Orchestrator(captured, transport, new SyncEventStream());
        await orchestrator.FlushAsync();

        // Round-tripped through JSON as a durable store would, which is the real test of the
        // decision to keep exceptions out of the record: an Exception would not survive this.
        var restored = captured.LastSnapshotAsRestored();
        var outcome = Assert.IsType<SyncOutcome>(restored.LastOutcome);

        Assert.Equal("sale-42", restored.CorrelationId);
        Assert.Equal(SyncOutcomeKind.Rejected, outcome.Kind);
        Assert.True(outcome.IsFinal);
        Assert.Equal(409, outcome.StatusCode);
        Assert.Contains("Only 20 left", outcome.GetBodyAsText());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Envelope Outbox(string correlationId, string? body = null) =>
        new()
        {
            Url = Url,
            Method = "POST",
            CorrelationId = correlationId,
            RequestBody = body,
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

    private static SyncOrchestrator Orchestrator(
        ISyncStore store,
        StubHttpMessageHandler transport,
        SyncEventStream events,
        FakeSyncPolicy? policy = null,
        HyperwycOptions? options = null) =>
        new(store,
            policy ?? new FakeSyncPolicy(),
            new FakeConnectivityService(isConnected: true),
            events,
            options ?? new HyperwycOptions { ConnectivityDebounceDelay = TimeSpan.Zero },
            transport);

    private static ServiceProvider BuildOfflineClient(
        InMemorySyncStore store, out IHttpClientFactory factory)
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

    private static List<SyncEvent> Collect(IServiceProvider sp)
    {
        var events = new List<SyncEvent>();
        sp.GetRequiredService<IHyperwyc>().SyncEvents.Subscribe(new Collector(events));
        return events;
    }

    private sealed class Collector(List<SyncEvent> collected) : IObserver<SyncEvent>
    {
        public void OnNext(SyncEvent value) => collected.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>
    /// Delegates to an <see cref="InMemorySyncStore"/> while snapshotting each upsert as JSON,
    /// so a test can read back what a durable store would actually have written rather than
    /// the same object it handed in.
    /// </summary>
    private sealed class OutcomeCapturingStore(InMemorySyncStore inner) : ISyncStore
    {
        private string? _lastSnapshot;

        public Envelope LastSnapshotAsRestored() =>
            JsonSerializer.Deserialize<Envelope>(_lastSnapshot!)!;

        public Task UpsertAsync(Envelope envelope, CancellationToken ct = default)
        {
            _lastSnapshot = JsonSerializer.Serialize(envelope);
            return inner.UpsertAsync(envelope, ct);
        }

        public Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default) =>
            inner.GetCachedResponseAsync(url, ct);

        public Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default) =>
            inner.GetPendingOutboxAsync(ct);

        public Task<IReadOnlyList<Envelope>> GetReadyToSendAsync(
            DateTimeOffset now, CancellationToken ct = default) =>
            inner.GetReadyToSendAsync(now, ct);

        public Task MarkSyncedAsync(string id, CancellationToken ct = default) =>
            inner.MarkSyncedAsync(id, ct);

        public Task MoveToDeadLetterAsync(string id, CancellationToken ct = default) =>
            inner.MoveToDeadLetterAsync(id, ct);

        public Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default) =>
            inner.InvalidateCacheForPrefixAsync(urlPrefix, ct);

        public Task ResetAsync(CancellationToken ct = default) => inner.ResetAsync(ct);
    }
}
