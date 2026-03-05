using Polly;
using Polly.Retry;
using Restyc.Interfaces;
using Restyc.Models;

namespace Restyc;

/// <summary>
/// Listens for connectivity restoration, drains the outbox in order, and
/// manages the single-flush semaphore and connectivity-event debounce.
/// </summary>
/// <remarks>
/// Call <see cref="FlushAsync"/> directly to trigger a manual sync (e.g. from
/// a UI "sync now" button). The orchestrator also reacts automatically to
/// <see cref="IConnectivityService.ConnectivityChanged"/> events with a
/// configurable debounce delay (<see cref="RestycOptions.ConnectivityDebounceDelay"/>).
/// </remarks>
public sealed class SyncOrchestrator : IAsyncDisposable
{
    private const string _idempotencyKeyHeader = "Idempotency-Key";

    private readonly ISyncStore _store;
    private readonly Interfaces.ISyncPolicy _policy;
    private readonly IConnectivityService _connectivity;
    private readonly SyncEventStream _events;
    private readonly RestycOptions _options;
    private readonly HttpMessageInvoker _invoker;

    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly IDisposable _connectivitySubscription;

    private CancellationTokenSource? _debounceCts;
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
    /// The inner <see cref="HttpMessageHandler"/> used to send outbox requests.
    /// This should be a bare transport handler that bypasses <see cref="RestycHandler"/>.
    /// </param>
    public SyncOrchestrator(
        ISyncStore store,
        Interfaces.ISyncPolicy policy,
        IConnectivityService connectivity,
        SyncEventStream events,
        RestycOptions options,
        HttpMessageHandler transport)
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
        _invoker = new HttpMessageInvoker(transport, disposeHandler: false);

        _connectivitySubscription = connectivity.ConnectivityChanged
            .Subscribe(new ConnectivityObserver(this));
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Drains the outbox, sending each pending envelope to the server in
    /// <see cref="Models.Envelope.CreatedUtc"/> ascending order.
    /// If a flush is already in progress this call returns immediately.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Non-blocking attempt: if a flush is already running, skip.
        if (!await _flushGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            var pending = await _store.GetPendingOutboxAsync(ct).ConfigureAwait(false);

            foreach (var envelope in pending)
            {
                if (ct.IsCancellationRequested) break;
                await SendWithRetryAsync(envelope, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Send + Polly retry pipeline
    // -------------------------------------------------------------------------

    private async Task SendWithRetryAsync(Envelope envelope, CancellationToken ct)
    {
        // Determine retry configuration from the policy once per envelope.
        var probeRequest = BuildRequest(envelope);
        var retryOptions = _policy.GetRetryOptions(probeRequest);
        probeRequest.Dispose();

        var pipeline = BuildRetryPipeline(retryOptions, envelope);

        HttpResponseMessage? finalResponse = null;
        try
        {
            finalResponse = await pipeline.ExecuteAsync(async token =>
            {
                var request = BuildRequest(envelope);
                return await _invoker.SendAsync(request, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) { /* all retries exhausted on exception */ }

        if (finalResponse?.IsSuccessStatusCode == true)
        {
            await _store.MarkSyncedAsync(envelope.Id, ct).ConfigureAwait(false);

            _events.Publish(new SyncEvent(
                SyncEventType.OnSynced,
                envelope.Url,
                envelope.Method,
                DateTimeOffset.UtcNow));

            var lastRequest = BuildRequest(envelope);
            if (_policy.ShouldInvalidateCacheOnWrite(lastRequest))
            {
                var prefix = RestycHandler.DeriveInvalidationPrefix(lastRequest.RequestUri);
                await _store.InvalidateCacheForPrefixAsync(prefix, ct).ConfigureAwait(false);
            }
            lastRequest.Dispose();
        }
        else
        {
            await _store.MoveToDeadLetterAsync(envelope.Id, ct).ConfigureAwait(false);

            _events.Publish(new SyncEvent(
                SyncEventType.OnFailed,
                envelope.Url,
                envelope.Method,
                DateTimeOffset.UtcNow));
        }
    }

    private ResiliencePipeline<HttpResponseMessage> BuildRetryPipeline(
        RetryOptions retryOptions,
        Envelope envelope)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();

        // Polly requires MaxRetryAttempts >= 1; skip when no retries are configured.
        if (retryOptions.MaxRetries >= 1)
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = retryOptions.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = retryOptions.InitialDelay > TimeSpan.Zero
                    ? retryOptions.InitialDelay
                    : TimeSpan.FromMilliseconds(1),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r => !r.IsSuccessStatusCode),
                OnRetry = args =>
                {
                    _events.Publish(new SyncEvent(
                        SyncEventType.OnRetrying,
                        envelope.Url,
                        envelope.Method,
                        DateTimeOffset.UtcNow));
                    return default;
                },
            });
        }

        return builder.Build();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HttpRequestMessage BuildRequest(Envelope envelope)
    {
        var method = new HttpMethod(envelope.Method);
        var request = new HttpRequestMessage(method, envelope.Url);

        foreach (var (key, value) in envelope.RequestHeaders)
        {
            if (!request.Headers.TryAddWithoutValidation(key, value))
                request.Content?.Headers.TryAddWithoutValidation(key, value);
        }

        // Re-inject the idempotency key from the envelope ID so retries are idempotent.
        if (!request.Headers.Contains(_idempotencyKeyHeader))
            request.Headers.TryAddWithoutValidation(_idempotencyKeyHeader, envelope.Id);

        if (envelope.RequestBody is not null)
            request.Content = new StringContent(envelope.RequestBody);

        return request;
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
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _connectivitySubscription.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        // Drain the semaphore so any in-flight flush can finish.
        await _flushGate.WaitAsync().ConfigureAwait(false);
        _flushGate.Release();
        _flushGate.Dispose();
        _invoker.Dispose();
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
