using System.Net;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Listens for connectivity restoration, drains the outbox in order, and
/// manages the single-flush semaphore.
/// </summary>
/// <remarks>
/// Internal: consumers reach flushing through <see cref="IHyperwyc.FlushAsync"/> rather than
/// depending on this type. It flushes on exactly two triggers — application start, and
/// <see cref="IConnectivityService.ConnectivityChanged"/> reporting connectivity restored.
/// There is no retry budget, no backoff and no scheduled follow-up: a write the server answers
/// is done with and discarded, and only one it could not reach stays in the outbox for the next
/// flush.
/// </remarks>
internal sealed class OutboxProcessor : IDisposable, IAsyncDisposable
{
    private readonly IHyperwycStore _store;
    private readonly IConnectivityService _connectivity;
    private readonly HyperwycEventStream _events;
    private readonly HyperwycOptions _options;
    private readonly StoreHealth _health;
    private readonly HttpMessageInvoker _fallbackInvoker;
    private readonly IHttpClientFactory? _httpClientFactory;

    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly IDisposable _connectivitySubscription;

    /// <summary>
    /// Cancelled by either disposal path. Every flush links to this, so disposal
    /// stops work started by connectivity, by startup, or by a manual call.
    /// </summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    private bool _disposed;

    /// <summary>
    /// Initialises a new <see cref="OutboxProcessor"/>.
    /// </summary>
    /// <param name="store">The sync store backing the outbox.</param>
    /// <param name="connectivity">The connectivity service to subscribe to.</param>
    /// <param name="events">The event stream to publish lifecycle events on.</param>
    /// <param name="options">Runtime configuration options.</param>
    /// <param name="health">Shared state tracking whether the store can be read.</param>
    /// <param name="transport">
    /// The <see cref="HttpMessageHandler"/> used to send outbox requests. This must
    /// bypass <see cref="HyperwycHandler"/>, or a replay would be queued again.
    /// Never disposed by the processor — see
    /// <see cref="HyperwycOptions.ReplayTransport"/> for ownership.
    /// </param>
    /// <param name="httpClientFactory">
    /// Used to replay a write through the named client it was queued on, so that
    /// downstream handlers such as auth apply to replays. When absent, or when a
    /// write carries no client name, <paramref name="transport"/> is used instead.
    /// </param>
    public OutboxProcessor(
        IHyperwycStore store,
        IConnectivityService connectivity,
        HyperwycEventStream events,
        HyperwycOptions options,
        HttpMessageHandler transport,
        StoreHealth health,
        IHttpClientFactory? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connectivity);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(health);

        _store = store;
        _connectivity = connectivity;
        _events = events;
        _options = options;
        _health = health;
        _fallbackInvoker = new HttpMessageInvoker(transport, disposeHandler: false);
        _httpClientFactory = httpClientFactory;

        _connectivitySubscription = connectivity.ConnectivityChanged
            .Subscribe(new ConnectivityObserver(this));
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Drains the outbox, making one delivery attempt per queued write in
    /// <see cref="Models.QueuedWrite.CreatedUtc"/> ascending order.
    /// If a flush is already in progress this call returns immediately.
    /// </summary>
    /// <remarks>
    /// One attempt each, not a retry loop, and nothing is scheduled. Any answer from the
    /// server is final and the write is discarded, whatever the status. Only a transport
    /// failure leaves a write queued — and it ends the flush rather than moving to the next
    /// one, because an unusable network is a fact about those too.
    /// </remarks>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Link the caller's token with the processor's lifetime so that disposal
        // stops a flush no matter how it was started — including a manual "sync now"
        // that passed no token of its own.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        var token = linked.Token;

        // Non-blocking attempt: if a flush is already running, skip.
        if (!await _flushGate.WaitAsync(0, token).ConfigureAwait(false))
            return;

        try
        {
            IReadOnlyList<QueuedWrite> ready;
            var generation = _health.Generation;
            try
            {
                ready = await _store.GetPendingOutboxAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (StoreHealth.IsStoreFailure(ex))
            {
                // Nothing to flush if the outbox cannot be read, and nothing to be done about it
                // here. Reporting may set the store aside and start a clean one, and there is
                // still nothing to flush — the new one is empty.
                await _health.ReportUnreadableAsync(ex, generation, token).ConfigureAwait(false);
                return;
            }

            foreach (var write in ready)
            {
                if (token.IsCancellationRequested) break;

                var outcome = await SendAsync(write, token).ConfigureAwait(false);

                if (outcome == SendOutcome.ConnectivityLost)
                {
                    // The premise of this flush was that the network is reachable. It is
                    // not, so the remaining writes would fail for the same reason.
                    // Stop and wait to be told connectivity has returned.
                    break;
                }

            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>
    /// Clears the store, waiting for any flush already running to finish first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives here rather than on <c>HyperwycService</c> because it needs the flush gate, which
    /// only the processor has.
    /// </para>
    /// <para>
    /// The gate is acquired <em>blocking</em>, unlike <see cref="FlushAsync"/>'s
    /// try-acquire. A flush that is mid-loop holds a list of writes read before the wipe
    /// and keeps acting on them: it would go on sending writes the caller just asked to
    /// discard, and — worse — <c>RecordOutcomeAsync</c> writes back, so a
    /// transport-failed write would be <em>re-inserted</em> into a store that had just
    /// been emptied. On the logout this method exists for, that resurrects the previous
    /// user's data.
    /// </para>
    /// <para>
    /// It deliberately does not flush first. Reset means discard; an application that wants
    /// its queued writes delivered calls <see cref="FlushAsync"/> itself, beforehand, while
    /// it still has whatever credentials the replays need.
    /// </para>
    /// </remarks>
    internal async Task ResetStoreAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        var token = linked.Token;

        await _flushGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _store.ResetAsync(token).ConfigureAwait(false);

            // Whatever made the store unreadable is gone with its contents, so caching and
            // queueing resume rather than staying disabled for the life of the process.
            _health.Recovered();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>
    /// Reads the outbox and projects each entry to its diagnostic view.
    /// </summary>
    /// <remarks>
    /// Guards the store read the same way <see cref="FlushAsync"/> does: an unreadable store is
    /// reported through <see cref="StoreHealth"/> and the caller gets an empty list, matching the
    /// "Hyperwyc quietly stands aside when the store is broken" behaviour every other read path
    /// takes. <see cref="HyperwycEventType.OnStoreQuarantined"/> and
    /// <see cref="HyperwycEventType.OnStoreUnreadable"/> are how a diagnostics UI learns the
    /// store itself is the problem — and, in the first case, that the outbox it is showing is
    /// empty because the previous one was set aside rather than because nothing was queued.
    /// </remarks>
    internal async Task<IReadOnlyList<PendingItem>> GetDiagnosticViewAsync(CancellationToken ct = default)
    {
        IReadOnlyList<QueuedWrite> outbox;
        var generation = _health.Generation;
        try
        {
            outbox = await _store.GetPendingOutboxAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (StoreHealth.IsStoreFailure(ex))
        {
            await _health.ReportUnreadableAsync(ex, generation, ct).ConfigureAwait(false);
            return [];
        }

        return [.. outbox.Select(PendingItem.From)];
    }

    // -------------------------------------------------------------------------
    // Sending
    // -------------------------------------------------------------------------

    /// <summary>
    /// What one delivery attempt concluded, and therefore what the flush should do next.
    /// </summary>
    private enum SendOutcome
    {
        /// <summary>The server answered; the write is gone from the store.</summary>
        Delivered,

        /// <summary>The network is unreachable, so the rest of the flush is pointless.</summary>
        ConnectivityLost,
    }

    /// <summary>
    /// Makes a single delivery attempt. A write stays in the outbox only when no response
    /// was received; any answer from the server is a final outcome.
    /// </summary>
    private async Task<SendOutcome> SendAsync(QueuedWrite write, CancellationToken ct)
    {
        using var request = BuildRequest(write);

        HttpResponseMessage response;
        try
        {
            response = await ResolveInvoker(write).SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // The device believed it was online but the network is not usable — a captive
            // portal, DNS failure, or signal that dropped mid-flush. Every remaining
            // write would fail identically, so report it and let the flush stop.
            //
            // Recorded on the write even though no event fires: "last attempt could not
            // reach the host" is exactly what a diagnostics view needs to explain an outbox
            // that is not draining. See issue 23 — it is the only place this is visible.
            await RecordOutcomeAsync(write, TransportOutcome(ex), ct).ConfigureAwait(false);
            return SendOutcome.ConnectivityLost;
        }

        using (response)
        {
            // The server answered. Whatever it said, the request reached the API — which was
            // Hyperwyc's whole job, and it is done. One path, not two: a refusal and an
            // acceptance are the same event from here, and treating them differently was the
            // store deciding what a status code means on the application's behalf. See ADR 0010.
            //
            // Retrying a 5xx here would be a worse retry than the one that already ran: the
            // replay traverses the application's pipeline, so its own resilience handler has
            // had its turn, on a schedule that tracks the failure. Hyperwyc's only trigger is
            // a connectivity change, which is uncorrelated with a server recovering — and for
            // a device that never goes offline again, never arrives at all. A write kept on
            // that promise is a write kept forever. See ADR 0001.
            //
            // The body is read whatever the status. On a rejection it is usually the only
            // account of why; on a success a replayed POST may answer with the created
            // resource — server-assigned ids, normalised values — which the caller never saw.
            // Either way this event is the only place it appears.
            var outcome = await OutcomeFromAsync(
                response, DeliveryOutcomeKind.Delivered, ct).ConfigureAwait(false);

            await DeliverAsync(write, outcome, response.IsSuccessStatusCode, ct).ConfigureAwait(false);
            return SendOutcome.Delivered;
        }
    }

    // -------------------------------------------------------------------------
    // Outcome capture
    //
    // A delivery outcome is published and not persisted: the write is gone by then, and what
    // the server said is an ordinary HTTP response that the application owns rather than
    // Hyperwyc. The event is therefore the only report of it — see ADR 0010, and note that this
    // is why FlushOnStartup defaults to false.
    //
    // A transport failure is the other way round. It publishes nothing and is written to the
    // queued write, which is still there, because an outbox that is not draining has to be able
    // to explain itself after a restart. See issue 23.
    // -------------------------------------------------------------------------

    /// <summary>Builds a <see cref="DeliveryOutcome"/> from a response, reading a capped body.</summary>
    private async Task<DeliveryOutcome> OutcomeFromAsync(
        HttpResponseMessage response,
        DeliveryOutcomeKind kind,
        CancellationToken ct)
    {
        var (body, truncated) = await ReadCappedBodyAsync(
            response.Content, _options.MaxOutcomeBodyBytes, ct).ConfigureAwait(false);

        return new DeliveryOutcome
        {
            Kind = kind,
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Body = body,
            BodyTruncated = truncated,
            OccurredUtc = DateTimeOffset.UtcNow,
        };
    }

    private static DeliveryOutcome TransportOutcome(HttpRequestException ex) =>
        new()
        {
            Kind = DeliveryOutcomeKind.TransportFailure,
            Error = ex.Message,
            OccurredUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Reads at most <paramref name="cap"/> bytes of <paramref name="content"/>, reporting
    /// whether there was more.
    /// </summary>
    /// <remarks>
    /// Streamed rather than <c>ReadAsByteArrayAsync</c> so a pathological error body cannot be
    /// pulled into memory in full just to be thrown away. A read that fails yields no body:
    /// losing the explanation is bad, but failing the flush over it would be worse.
    /// </remarks>
    private static async Task<(byte[]? Body, bool Truncated)> ReadCappedBodyAsync(
        HttpContent? content, int cap, CancellationToken ct)
    {
        if (content is null || cap <= 0)
            return (null, false);

        try
        {
            using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];

            // Reads one chunk beyond the cap at most, which is how truncation is detected.
            int read;
            while (buffer.Length <= cap
                && (read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length == 0)
                return (null, false);

            var bytes = buffer.ToArray();
            return bytes.Length > cap ? (bytes[..cap], true) : (bytes, false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            return (null, false);
        }
    }

    /// <summary>Persists <paramref name="outcome"/> against the queued write.</summary>
    private async Task RecordOutcomeAsync(QueuedWrite write, DeliveryOutcome outcome, CancellationToken ct)
    {
        write.LastOutcome = outcome;
        write.RetryCount++;
        await _store.UpsertQueuedWriteAsync(write, ct).ConfigureAwait(false);
    }

    private static HyperwycEvent EventFor(HyperwycEventType type, QueuedWrite write, DeliveryOutcome? outcome) =>
        new()
        {
            Type          = type,
            Url           = write.Url,
            Method        = write.Method,
            Timestamp     = DateTimeOffset.UtcNow,
            CorrelationId = write.CorrelationId,
            RequestId     = write.Id,
            RequestBody   = write.RequestBody,
            Outcome       = outcome,
        };

    /// <summary>
    /// Discards the delivered write, publishes the outcome, and invalidates the cache if the
    /// route asks for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record goes first and the event follows, which is the right order: a crash between
    /// them loses the notification, whereas the reverse would announce a delivery for a write
    /// still sitting in the outbox and about to go out again.
    /// </para>
    /// <para>
    /// <paramref name="succeeded"/> is the one place the status code is still read, and it is not
    /// the rejected/succeeded distinction coming back. Invalidation is a cache-freshness
    /// judgement about Hyperwyc's own data, and it follows what HTTP specifies for one — RFC 9111
    /// §4.4 invalidates on a <em>non-error</em> response to an unsafe method. Dropping cached
    /// reads because a write came back <c>422</c> would cost an offline read for nothing.
    /// </para>
    /// </remarks>
    private async Task DeliverAsync(
        QueuedWrite write, DeliveryOutcome outcome, bool succeeded, CancellationToken ct)
    {
        await _store.RemoveDeliveredAsync(write.Id, ct).ConfigureAwait(false);

        _events.Publish(EventFor(HyperwycEventType.OnDelivered, write, outcome));

        if (!succeeded) return;

        using var request = BuildRequest(write);
        if (_options.Routes.PolicyFor(request.RequestUri).InvalidateCacheOnWrite)
        {
            var prefix = HyperwycHandler.DeriveInvalidationPrefix(request.RequestUri);
            await _store.InvalidateCacheForPrefixAsync(prefix, ct).ConfigureAwait(false);
        }
    }


    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the invoker a replay should be sent through: the originating named
    /// client's full pipeline where one is known, otherwise the configured fallback
    /// transport.
    /// </summary>
    /// <remarks>
    /// Going back through the originating client is what lets downstream handlers —
    /// auth, logging, telemetry — apply to replays exactly as they do to ordinary
    /// requests. <see cref="HyperwycHandler"/> recognises the replay marker and steps
    /// aside, so the request is not intercepted a second time.
    /// </remarks>
    private HttpMessageInvoker ResolveInvoker(QueuedWrite write)
    {
        if (_httpClientFactory is not null && !string.IsNullOrEmpty(write.ClientName))
            return _httpClientFactory.CreateClient(write.ClientName);

        return _fallbackInvoker;
    }

    private static HttpRequestMessage BuildRequest(QueuedWrite write)
    {
        var method = new HttpMethod(write.Method);
        var request = new HttpRequestMessage(method, write.Url);

        // Applied per attempt: BuildRequest constructs a fresh message each retry, so
        // the marker cannot be assumed to carry over.
        request.Options.Set(HyperwycHandler.ReplayMarker, true);

        // Content first. The header loop below falls back to the content headers for anything
        // the request headers reject — Content-Type above all — and that fallback is silently
        // a no-op while Content is still null. Setting the body afterwards is what dropped the
        // media type on every replay, so a queued application/json write went back out as
        // text/plain and the server answered 415.
        // ByteArrayContent, so the bytes go back out exactly as they came in, and — unlike
        // StringContent — it stamps no Content-Type of its own, leaving the captured headers
        // below as the single source of truth.
        if (write.RequestBody is not null)
            request.Content = new ByteArrayContent(write.RequestBody);

        // Replayed verbatim, including anything the application set for its own
        // duplicate suppression. Hyperwyc adds nothing of its own.
        foreach (var (key, value) in write.RequestHeaders)
        {
            // Content-Length describes the body being sent, not the caller's intent, and
            // HttpClient computes it. Replaying a captured value risks contradicting the
            // content actually attached.
            if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!request.Headers.TryAddWithoutValidation(key, value))
                request.Content?.Headers.TryAddWithoutValidation(key, value);
        }

        return request;
    }

    // -------------------------------------------------------------------------
    // Connectivity
    // -------------------------------------------------------------------------

    /// <remarks>
    /// No debounce. <see cref="FlushAsync"/> try-acquires the flush gate and returns
    /// immediately if one is already running, so a burst of connectivity signals is already a
    /// no-op after the first — a timer to suppress them was guarding a cost that does not
    /// exist. Fire-and-forget because this runs on the observer's thread, which must not block.
    /// </remarks>
    private void OnConnectivityRestored() =>
        _ = Task.Run(async () =>
        {
            try
            {
                await FlushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* disposed */ }
            catch (ObjectDisposedException) { /* disposed mid-flight */ }
        });

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    /// <summary>
    /// Stops the processor without waiting for an in-flight flush to unwind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shutdown is deliberately not a flush trigger. Writes are queued only
    /// because connectivity was poor, and shutting down does not improve
    /// connectivity — anything still queued is replayed at next start. An
    /// interrupted flush therefore costs nothing but the current attempt.
    /// </para>
    /// <para>
    /// This is required in addition to <see cref="DisposeAsync"/> because
    /// Microsoft.Extensions.DependencyInjection refuses to dispose an
    /// <see cref="IAsyncDisposable"/>-only singleton from a synchronous
    /// <c>ServiceProvider.Dispose()</c>, which would throw at shutdown for any
    /// application using <c>using</c> rather than <c>await using</c>.
    /// </para>
    /// </remarks>
    public void Dispose() => Shutdown();

    /// <summary>
    /// Stops the processor and waits for an in-flight flush to observe
    /// cancellation before returning.
    /// </summary>
    /// <remarks>
    /// The wait is bounded by cancellation: the flush loop breaks at its next
    /// write boundary and any in-progress send is cancelled. This does not
    /// wait for queued work to finish
    /// sending — see <see cref="Dispose"/> for why shutdown is not a flush trigger.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        Shutdown();

        // Acquiring the gate proves no flush is running. Reached quickly because
        // Shutdown has already cancelled it.
        try
        {
            await _flushGate.WaitAsync().ConfigureAwait(false);
            _flushGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Raced a concurrent disposal; nothing left to wait for.
        }
    }

    /// <summary>
    /// The teardown both disposal paths share: stop listening, cancel everything
    /// in flight, and mark the processor disposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent, so disposing twice — or by both routes — is safe.
    /// </para>
    /// <para>
    /// Note what is deliberately <em>not</em> disposed here: <c>_flushGate</c> and
    /// <c>_invoker</c>. A flush may still be unwinding and would fault on either,
    /// surfacing an <see cref="ObjectDisposedException"/> on a fire-and-forget task
    /// during shutdown. Neither holds a resource that requires release — the
    /// semaphore's <c>AvailableWaitHandle</c> is never used, and the invoker was
    /// constructed with <c>disposeHandler: false</c>, so it does not own its
    /// transport.
    /// </para>
    /// </remarks>
    private void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;

        _connectivitySubscription.Dispose();



        _lifetimeCts.Cancel();
    }

    // -------------------------------------------------------------------------
    // Nested observer
    // -------------------------------------------------------------------------

    private sealed class ConnectivityObserver(OutboxProcessor owner) : IObserver<bool>
    {
        public void OnNext(bool connected)
        {
            if (connected)
                owner.OnConnectivityRestored();
        }

        public void OnError(Exception error) { /* not actionable */ }
        public void OnCompleted() { }
    }
}
