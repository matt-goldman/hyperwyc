using Restyc.Interfaces;
using Restyc.Models;

namespace Restyc;

/// <summary>
/// <see cref="DelegatingHandler"/> that routes HTTP requests through Restyc's
/// caching and offline-sync pipeline.
/// </summary>
/// <remarks>
/// This handler must be placed inside the <see cref="HttpClient"/> pipeline
/// (i.e. closer to the transport than auth handlers). Assign an
/// <see cref="System.Net.Http.HttpMessageHandler.InnerHandler"/> or use the DI
/// extension from <c>Restyc.Extensions</c> which wires this up automatically.
/// </remarks>
public sealed class RestycHandler : DelegatingHandler
{
    private static readonly HashSet<HttpMethod> WriteMethods =
    [
        HttpMethod.Post,
        HttpMethod.Put,
        HttpMethod.Patch,
        HttpMethod.Delete,
    ];

    private readonly ISyncStore _store;
    private readonly IConnectivityService _connectivity;
    private readonly ISyncPolicy _policy;
    private readonly IStalenessEvaluator _stalenessEvaluator;
    private readonly SyncEventStream _events;

    /// <summary>
    /// Initialises a new <see cref="RestycHandler"/>.
    /// </summary>
    public RestycHandler(
        ISyncStore store,
        IConnectivityService connectivity,
        ISyncPolicy policy,
        IStalenessEvaluator stalenessEvaluator,
        SyncEventStream events,
        RestycOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connectivity);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(stalenessEvaluator);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _connectivity = connectivity;
        _policy = policy;
        _stalenessEvaluator = stalenessEvaluator;
        _events = events;
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_connectivity.IsConnected)
            return HandleOnlineAsync(request, cancellationToken);

        // Offline path — issue #07
        throw new NotSupportedException("Offline path not yet implemented (see issue #07).");
    }

    // -------------------------------------------------------------------------
    // Online paths
    // -------------------------------------------------------------------------

    private Task<HttpResponseMessage> HandleOnlineAsync(HttpRequestMessage request, CancellationToken ct) =>
        WriteMethods.Contains(request.Method)
            ? HandleOnlineWriteAsync(request, ct)
            : HandleOnlineReadAsync(request, ct);

    private async Task<HttpResponseMessage> HandleOnlineWriteAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (_policy.ShouldInvalidateCacheOnWrite(request))
                await _store.InvalidateCacheForPrefixAsync(url, ct).ConfigureAwait(false);

            _events.Publish(new SyncEvent(
                SyncEventType.OnSynced, url, request.Method.Method, DateTimeOffset.UtcNow));
        }

        return response;
    }

    private async Task<HttpResponseMessage> HandleOnlineReadAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        var cached = await _store.GetCachedResponseAsync(url, ct).ConfigureAwait(false);

        if (cached is not null && !_stalenessEvaluator.IsStale(cached, DateTimeOffset.UtcNow))
            return BuildResponseFromEnvelope(cached);

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            // Buffer content so ForCachedResponse can read it synchronously and
            // the caller can still read the body afterwards.
            if (response.Content is not null)
                await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);

            var envelope = Envelope.ForCachedResponse(request, response);
            await _store.UpsertAsync(envelope, ct).ConfigureAwait(false);

            _events.Publish(new SyncEvent(
                SyncEventType.OnUpdated, url, request.Method.Method, DateTimeOffset.UtcNow));
        }

        return response;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HttpResponseMessage BuildResponseFromEnvelope(Envelope envelope)
    {
        var cached = envelope.Response!;
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)cached.StatusCode);

        if (cached.Body is not null)
            response.Content = new StringContent(cached.Body);

        return response;
    }
}
