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
    private const string _idempotencyKeyHeader = "Idempotency-Key";

    private static readonly HashSet<HttpMethod> _writeMethods =
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
    private readonly RestycOptions _options;

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
        _options = options;
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_connectivity.IsConnected)
            return HandleOnlineAsync(request, cancellationToken);

        return HandleOfflineAsync(request, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Offline paths
    // -------------------------------------------------------------------------

    private Task<HttpResponseMessage> HandleOfflineAsync(HttpRequestMessage request, CancellationToken ct) =>
        _writeMethods.Contains(request.Method)
            ? HandleOfflineWriteAsync(request, ct)
            : HandleOfflineReadAsync(request, ct);

    private async Task<HttpResponseMessage> HandleOfflineWriteAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        // Buffer content before the synchronous read inside Envelope.ForRequest.
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);

        EnsureIdempotencyKey(request);
        var envelope = Envelope.ForRequest(request);
        await _store.UpsertAsync(envelope, ct).ConfigureAwait(false);

        _events.Publish(new SyncEvent(
            SyncEventType.OnQueued,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Method.Method,
            DateTimeOffset.UtcNow));

        return RestycResponseFactory.Queued();
    }

    private async Task<HttpResponseMessage> HandleOfflineReadAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;

        // Serve from cache even if stale — any cached data is better than nothing offline.
        var cached = await _store.GetCachedResponseAsync(url, ct).ConfigureAwait(false);
        if (cached is not null)
            return BuildResponseFromEnvelope(cached);

        return RestycResponseFactory.Offline();
    }

    // -------------------------------------------------------------------------
    // Online paths
    // -------------------------------------------------------------------------

    private Task<HttpResponseMessage> HandleOnlineAsync(HttpRequestMessage request, CancellationToken ct) =>
        _writeMethods.Contains(request.Method)
            ? HandleOnlineWriteAsync(request, ct)
            : HandleOnlineReadAsync(request, ct);

    private async Task<HttpResponseMessage> HandleOnlineWriteAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        EnsureIdempotencyKey(request);
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (_policy.ShouldInvalidateCacheOnWrite(request))
            {
                var prefix = DeriveInvalidationPrefix(request.RequestUri);
                await _store.InvalidateCacheForPrefixAsync(prefix, ct).ConfigureAwait(false);
            }

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
                await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);

            var bodyLength = response.Content?.Headers.ContentLength ?? 0;
            if (bodyLength <= _options.MaxCachedResponseBodyBytes)
            {
                var envelope = Envelope.ForCachedResponse(request, response);
                await _store.UpsertAsync(envelope, ct).ConfigureAwait(false);

                _events.Publish(new SyncEvent(
                    SyncEventType.OnUpdated, url, request.Method.Method, DateTimeOffset.UtcNow));
            }
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

        foreach (var (key, value) in cached.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(key, value))
                response.Content?.Headers.TryAddWithoutValidation(key, value);
        }

        return response;
    }

    /// <summary>
    /// Ensures a stable <c>Idempotency-Key</c> header exists on
    /// <paramref name="request"/>. If the caller already supplied one it is
    /// left unchanged; otherwise a fresh GUID is injected.
    /// </summary>
    private static void EnsureIdempotencyKey(HttpRequestMessage request)
    {
        if (!request.Headers.Contains(_idempotencyKeyHeader))
            request.Headers.TryAddWithoutValidation(_idempotencyKeyHeader, Guid.NewGuid().ToString());
    }

    /// <summary>
    /// Derives the cache-invalidation prefix from the request URI.
    /// If the last path segment is a numeric ID or GUID (i.e. an individual
    /// resource identifier), the prefix is the parent collection path so that
    /// all related cached entries are invalidated together.
    /// </summary>
    internal static string DeriveInvalidationPrefix(Uri? requestUri)
    {
        if (requestUri is null)
            return string.Empty;

        var path = requestUri.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');

        if (lastSlash > 0)
        {
            var lastSegment = path[(lastSlash + 1)..];
            if (long.TryParse(lastSegment, out _) || Guid.TryParse(lastSegment, out _))
            {
                var parentPath = path[..lastSlash];
                return requestUri.GetLeftPart(UriPartial.Authority) + parentPath;
            }
        }

        // No resource-identifier suffix — use the full URL (without query string).
        return requestUri.GetLeftPart(UriPartial.Authority) + path;
    }
}
