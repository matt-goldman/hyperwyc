using System.Net;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Listens for connectivity restoration, drains the outbox in order, and
/// manages the single-flush semaphore and connectivity-event debounce.
/// </summary>
/// <remarks>
/// Internal: consumers reach flushing through <see cref="IHyperwyc.FlushAsync"/>
/// rather than depending on this type. The orchestrator reacts automatically to
/// <see cref="IConnectivityService.ConnectivityChanged"/> events with a
/// configurable debounce delay (<see cref="HyperwycOptions.ConnectivityDebounceDelay"/>).
/// </remarks>
internal sealed class SyncOrchestrator : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Ceiling on a computed backoff, so a generous retry budget cannot schedule an
    /// attempt absurdly far out — or overflow the arithmetic getting there.
    /// </summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly ISyncStore _store;
    private readonly Interfaces.ISyncPolicy _policy;
    private readonly IConnectivityService _connectivity;
    private readonly SyncEventStream _events;
    private readonly HyperwycOptions _options;
    private readonly HttpMessageInvoker _fallbackInvoker;
    private readonly IHttpClientFactory? _httpClientFactory;

    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly IDisposable _connectivitySubscription;

    /// <summary>
    /// Cancelled by either disposal path. Every flush links to this, so disposal
    /// stops work started by connectivity, by startup, or by a manual call.
    /// </summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _followUpCts;
    private bool _disposed;

    /// <summary>
    /// Initialises a new <see cref="SyncOrchestrator"/>.
    /// </summary>
    /// <param name="store">The sync store backing the outbox.</param>
    /// <param name="policy">The sync policy used for retry configuration and cache-invalidation rules.</param>
    /// <param name="connectivity">The connectivity service to subscribe to.</param>
    /// <param name="events">The event stream to publish lifecycle events on.</param>
    /// <param name="options">Runtime configuration options.</param>
    /// <param name="transport">
    /// The <see cref="HttpMessageHandler"/> used to send outbox requests. This must
    /// bypass <see cref="HyperwycHandler"/>, or a replay would be queued again.
    /// Never disposed by the orchestrator — see
    /// <see cref="HyperwycOptions.ReplayTransport"/> for ownership.
    /// </param>
    /// <param name="httpClientFactory">
    /// Used to replay an envelope through the named client it was queued on, so that
    /// downstream handlers such as auth apply to replays. When absent, or when an
    /// envelope carries no client name, <paramref name="transport"/> is used instead.
    /// </param>
    public SyncOrchestrator(
        ISyncStore store,
        Interfaces.ISyncPolicy policy,
        IConnectivityService connectivity,
        SyncEventStream events,
        HyperwycOptions options,
        HttpMessageHandler transport,
        IHttpClientFactory? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(connectivity);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);

        _store = store;
        _policy = policy;
        _connectivity = connectivity;
        _events = events;
        _options = options;
        _fallbackInvoker = new HttpMessageInvoker(transport, disposeHandler: false);
        _httpClientFactory = httpClientFactory;

        _connectivitySubscription = connectivity.ConnectivityChanged
            .Subscribe(new ConnectivityObserver(this));
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Drains the outbox, making one delivery attempt per eligible envelope in
    /// <see cref="Models.Envelope.CreatedUtc"/> ascending order.
    /// If a flush is already in progress this call returns immediately.
    /// </summary>
    /// <remarks>
    /// One attempt each, not a retry loop. An envelope that fails transiently is left
    /// queued with a scheduled next-attempt time, so a single undeliverable write cannot
    /// hold up everything behind it — which is what a per-envelope backoff loop does when
    /// the outbox drains sequentially.
    /// </remarks>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Link the caller's token with the orchestrator's lifetime so that disposal
        // stops a flush no matter how it was started — including a manual "sync now"
        // that passed no token of its own.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        var token = linked.Token;

        // Non-blocking attempt: if a flush is already running, skip.
        if (!await _flushGate.WaitAsync(0, token).ConfigureAwait(false))
            return;

        try
        {
            var ready = await _store.GetReadyToSendAsync(DateTimeOffset.UtcNow, token)
                .ConfigureAwait(false);

            foreach (var envelope in ready)
            {
                if (token.IsCancellationRequested) break;

                var outcome = await SendAsync(envelope, token).ConfigureAwait(false);

                if (outcome == SendOutcome.ConnectivityLost)
                {
                    // The premise of this flush was that the network is reachable. It is
                    // not, so the remaining envelopes would fail for the same reason.
                    // Stop and wait to be told connectivity has returned.
                    break;
                }

            }
        }
        finally
        {
            _flushGate.Release();
        }

        await ScheduleNextPassAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Schedules one further flush for when the earliest waiting envelope becomes
    /// eligible, if anything is waiting.
    /// </summary>
    /// <remarks>
    /// Derived from the store rather than from what this pass happened to defer. That
    /// matters: a follow-up timer can fire a moment early, find nothing ready and defer
    /// nothing, and if scheduling depended on deferrals it would schedule nothing
    /// further — stranding the envelope until the next connectivity change. Asking what
    /// is still waiting is correct regardless of why this pass deferred nothing.
    /// </remarks>
    private async Task ScheduleNextPassAsync(CancellationToken ct)
    {
        if (_disposed || ct.IsCancellationRequested) return;

        DateTimeOffset? nextDue;
        try
        {
            var pending = await _store.GetPendingOutboxAsync(ct).ConfigureAwait(false);
            nextDue = pending
                .Where(e => e.NextRetryUtc is not null)
                .Min(e => e.NextRetryUtc);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (nextDue is { } due)
            ScheduleFollowUp(due);
    }

    // -------------------------------------------------------------------------
    // Sending
    // -------------------------------------------------------------------------

    /// <summary>
    /// What one delivery attempt concluded, and therefore what the flush should do next.
    /// </summary>
    private enum SendOutcome
    {
        /// <summary>Delivered; the envelope is out of the outbox.</summary>
        Synced,

        /// <summary>Rejected or out of budget; the envelope will not be attempted again.</summary>
        DeadLettered,

        /// <summary>Transient failure; the envelope stays queued for a later attempt.</summary>
        Deferred,

        /// <summary>The network is unreachable, so the rest of the flush is pointless.</summary>
        ConnectivityLost,
    }

    /// <summary>
    /// Makes a single delivery attempt. Retrying is not this method's job — a failed
    /// attempt either dead-letters or is deferred to a later flush.
    /// </summary>
    private async Task<SendOutcome> SendAsync(Envelope envelope, CancellationToken ct)
    {
        using var request = BuildRequest(envelope);
        var retryOptions = _policy.GetRetryOptions(request);

        if (envelope.RetryCount > 0)
        {
            _events.Publish(new SyncEvent(
                SyncEventType.OnRetrying, envelope.Url, envelope.Method, DateTimeOffset.UtcNow));
        }

        HttpResponseMessage response;
        try
        {
            response = await ResolveInvoker(envelope).SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The device believed it was online but the network is not usable — a captive
            // portal, DNS failure, or signal that dropped mid-flush. Every remaining
            // envelope would fail identically, so report it and let the flush stop.
            return SendOutcome.ConnectivityLost;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                await MarkDeliveredAsync(envelope, ct).ConfigureAwait(false);
                return SendOutcome.Synced;
            }

            // A 4xx describes the request, not the connection. Replaying it unchanged
            // produces the same answer, and any failure the application knows how to
            // resolve — a token refresh, say — has already had its turn further down the
            // pipeline. Retrying here would only delay an outcome already known.
            if (IsPermanentFailure(response.StatusCode))
            {
                await DeadLetterAsync(envelope, ct).ConfigureAwait(false);
                return SendOutcome.DeadLettered;
            }

            return await DeferAsync(envelope, retryOptions, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether <paramref name="statusCode"/> means "this request will never succeed",
    /// as opposed to "not right now".
    /// </summary>
    private static bool IsPermanentFailure(HttpStatusCode statusCode) =>
        (int)statusCode is >= 400 and < 500
        && statusCode is not HttpStatusCode.RequestTimeout       // 408 — worth another go
        && statusCode is not HttpStatusCode.TooManyRequests;     // 429 — explicitly "later"

    private async Task MarkDeliveredAsync(Envelope envelope, CancellationToken ct)
    {
        await _store.MarkSyncedAsync(envelope.Id, ct).ConfigureAwait(false);

        _events.Publish(new SyncEvent(
            SyncEventType.OnSynced, envelope.Url, envelope.Method, DateTimeOffset.UtcNow));

        using var request = BuildRequest(envelope);
        if (_policy.ShouldInvalidateCacheOnWrite(request))
        {
            var prefix = HyperwycHandler.DeriveInvalidationPrefix(request.RequestUri);
            await _store.InvalidateCacheForPrefixAsync(prefix, ct).ConfigureAwait(false);
        }
    }

    private async Task DeadLetterAsync(Envelope envelope, CancellationToken ct)
    {
        await _store.MoveToDeadLetterAsync(envelope.Id, ct).ConfigureAwait(false);

        _events.Publish(new SyncEvent(
            SyncEventType.OnFailed, envelope.Url, envelope.Method, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Records a transient failure against the envelope and schedules when it becomes
    /// eligible again, or dead-letters it if the budget is spent.
    /// </summary>
    /// <remarks>
    /// The count and the next-attempt time are persisted rather than held in memory, so
    /// a budget survives the process being killed mid-flush — routine on mobile.
    /// </remarks>
    private async Task<SendOutcome> DeferAsync(
        Envelope envelope,
        RetryOptions retryOptions,
        CancellationToken ct)
    {
        envelope.RetryCount++;

        if (envelope.RetryCount > retryOptions.MaxRetries)
        {
            await DeadLetterAsync(envelope, ct).ConfigureAwait(false);
            return SendOutcome.DeadLettered;
        }

        envelope.NextRetryUtc = DateTimeOffset.UtcNow + BackoffFor(retryOptions, envelope.RetryCount);
        await _store.UpsertAsync(envelope, ct).ConfigureAwait(false);

        return SendOutcome.Deferred;
    }

    /// <summary>
    /// Exponential backoff with jitter, clamped so an over-generous retry budget cannot
    /// produce an absurd — or arithmetically invalid — delay.
    /// </summary>
    private static TimeSpan BackoffFor(RetryOptions retryOptions, int attempt)
    {
        // A configured zero is honoured as zero: "retry as soon as you can" is a
        // legitimate choice now that a retry is a later flush rather than a sleep.
        var initial = retryOptions.InitialDelay > TimeSpan.Zero
            ? retryOptions.InitialDelay
            : TimeSpan.Zero;

        var multiplier = retryOptions.BackoffMultiplier > 1 ? retryOptions.BackoffMultiplier : 1;
        var seconds = initial.TotalSeconds * Math.Pow(multiplier, Math.Max(0, attempt - 1));

        // Jitter spreads a fleet of clients that all reconnected at the same moment.
        seconds *= 0.85 + (Random.Shared.NextDouble() * 0.3);

        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
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
    private HttpMessageInvoker ResolveInvoker(Envelope envelope)
    {
        if (_httpClientFactory is not null && !string.IsNullOrEmpty(envelope.ClientName))
            return _httpClientFactory.CreateClient(envelope.ClientName);

        return _fallbackInvoker;
    }

    private static HttpRequestMessage BuildRequest(Envelope envelope)
    {
        var method = new HttpMethod(envelope.Method);
        var request = new HttpRequestMessage(method, envelope.Url);

        // Applied per attempt: BuildRequest constructs a fresh message each retry, so
        // the marker cannot be assumed to carry over.
        request.Options.Set(HyperwycHandler.ReplayMarker, true);

        // Replayed verbatim, including anything the application set for its own
        // duplicate suppression. Hyperwyc adds nothing of its own.
        foreach (var (key, value) in envelope.RequestHeaders)
        {
            if (!request.Headers.TryAddWithoutValidation(key, value))
                request.Content?.Headers.TryAddWithoutValidation(key, value);
        }

        if (envelope.RequestBody is not null)
            request.Content = new StringContent(envelope.RequestBody);

        return request;
    }

    // -------------------------------------------------------------------------
    // Follow-up pass
    // -------------------------------------------------------------------------

    /// <summary>
    /// Schedules one further flush for when the earliest deferred envelope becomes
    /// eligible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, a transient server failure would wait for the next connectivity
    /// change or app start — which may never come while the device sits happily online.
    /// A backend having a bad day is a narrower case than the offline one Hyperwyc exists
    /// for, but it is a real one, and "your write goes out when the outage ends" is the
    /// only defensible answer to it.
    /// </para>
    /// <para>
    /// This terminates: every deferral increments <see cref="Envelope.RetryCount"/>, so an
    /// envelope that keeps failing eventually dead-letters and stops being rescheduled.
    /// </para>
    /// </remarks>
    private void ScheduleFollowUp(DateTimeOffset dueAt)
    {
        if (_disposed) return;

        // A small buffer past the due time: timers can fire fractionally early, and a
        // near-miss costs a whole extra scheduling round-trip.
        var delay = dueAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(15);
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        var previous = Interlocked.Exchange(ref _followUpCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var cts = _followUpCts!;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await FlushAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* superseded, or disposed */ }
            catch (ObjectDisposedException) { /* disposed between the check and the flush */ }
        }, cts.Token);
    }

    // -------------------------------------------------------------------------
    // Connectivity debounce
    // -------------------------------------------------------------------------

    private void OnConnectivityRestored()
    {
        // Cancel any in-flight debounce timer and start a new one.
        var previous = Interlocked.Exchange(ref _debounceCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var cts = _debounceCts!;
        var delay = _options.ConnectivityDebounceDelay;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await FlushAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* debounce superseded */ }
        }, cts.Token);
    }

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    /// <summary>
    /// Stops the orchestrator without waiting for an in-flight flush to unwind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shutdown is deliberately not a flush trigger. Envelopes are queued only
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
    /// Stops the orchestrator and waits for an in-flight flush to observe
    /// cancellation before returning.
    /// </summary>
    /// <remarks>
    /// The wait is bounded by cancellation, not by the retry budget: the flush
    /// loop breaks at its next envelope boundary and any in-progress send and
    /// backoff delay are cancelled. This does not wait for queued work to finish
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
    /// in flight, and mark the orchestrator disposed.
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

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        _followUpCts?.Cancel();
        _followUpCts?.Dispose();

        _lifetimeCts.Cancel();
    }

    // -------------------------------------------------------------------------
    // Nested observer
    // -------------------------------------------------------------------------

    private sealed class ConnectivityObserver(SyncOrchestrator owner) : IObserver<bool>
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
