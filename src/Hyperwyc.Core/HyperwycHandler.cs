using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// <see cref="DelegatingHandler"/> that routes HTTP requests through Hyperwyc's
/// caching and offline-sync pipeline.
/// </summary>
/// <remarks>
/// Register this handler <b>first</b> on the client, with
/// <c>AddHyperwycHandler()</c> — closest to your calling code, furthest from the
/// network. Handlers added after it then run on ordinary requests <i>and</i> on
/// replays, which is what allows a queued write to be authenticated with a token
/// minted at replay time rather than at queue time. See
/// <c>docs/decisions/0002-replays-traverse-the-pipeline.md</c>.
/// </remarks>
public sealed class HyperwycHandler : DelegatingHandler
{
    /// <summary>
    /// Marks a request as a replay from the outbox, so this handler passes it
    /// straight through instead of intercepting it again.
    /// </summary>
    /// <remarks>
    /// Replays are sent through the application's own pipeline so that downstream
    /// handlers — auth above all — apply to them exactly as they do to ordinary
    /// requests. Only this handler steps aside. Inferring "this is a replay" from
    /// connectivity is not sufficient: a replay reaching the normal online path
    /// would publish a second <c>OnSynced</c> and re-run cache invalidation, work
    /// the orchestrator has already taken responsibility for.
    /// </remarks>
    internal static readonly HttpRequestOptionsKey<bool> ReplayMarker = new("Hyperwyc.Replay");

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
    private readonly HyperwycOptions _options;
    private readonly string? _clientName;

    /// <summary>
    /// Initialises a new <see cref="HyperwycHandler"/>.
    /// </summary>
    /// <param name="store">Persistence for queued writes and cached responses.</param>
    /// <param name="connectivity">Reports whether the device is online.</param>
    /// <param name="policy">Cache strategy and write-invalidation rules per request.</param>
    /// <param name="stalenessEvaluator">Decides whether a cached response is still fresh.</param>
    /// <param name="events">Stream on which sync lifecycle events are published.</param>
    /// <param name="options">Runtime configuration options.</param>
    /// <param name="clientName">
    /// The name of the <see cref="HttpClient"/> this handler is registered on, stamped
    /// onto queued envelopes so a replay can be sent back through the same pipeline.
    /// <see langword="null"/> when the handler was registered without a name, in which
    /// case replays fall back to <see cref="HyperwycOptions.ReplayTransport"/>. Use
    /// <c>AddHyperwycHandler()</c> to have this captured automatically.
    /// </param>
    public HyperwycHandler(
        ISyncStore store,
        IConnectivityService connectivity,
        ISyncPolicy policy,
        IStalenessEvaluator stalenessEvaluator,
        SyncEventStream events,
        HyperwycOptions options,
        string? clientName = null)
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
        _clientName = clientName;
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // A replay from the outbox: step aside so the rest of the pipeline runs.
        if (request.Options.TryGetValue(ReplayMarker, out var isReplay) && isReplay)
            return base.SendAsync(request, cancellationToken);

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

        var envelope = Envelope.ForRequest(request, _clientName);
        await _store.UpsertAsync(envelope, ct).ConfigureAwait(false);

        _events.Publish(new SyncEvent(
            SyncEventType.OnQueued,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Method.Method,
            DateTimeOffset.UtcNow));

        return HyperwycResponseFactory.Queued(_options.OfflineResponsePolicy);
    }

    private async Task<HttpResponseMessage> HandleOfflineReadAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        // NetworkOnly opts out of the cache entirely, so there is nothing to serve.
        if (_policy.GetStrategy(request) == CacheStrategy.NetworkOnly)
            return HyperwycResponseFactory.Offline(_options.OfflineResponsePolicy);

        var url = request.RequestUri?.ToString() ?? string.Empty;

        // Serve from cache even if stale — any cached data is better than nothing offline.
        var cached = await _store.GetCachedResponseAsync(url, ct).ConfigureAwait(false);
        if (cached is not null)
            return BuildResponseFromEnvelope(cached);

        return HyperwycResponseFactory.Offline(_options.OfflineResponsePolicy);
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
        var strategy = _policy.GetStrategy(request);
        var url = request.RequestUri?.ToString() ?? string.Empty;

        // NetworkOnly neither reads nor writes the cache.
        if (strategy == CacheStrategy.NetworkOnly)
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        // CacheOnly never reaches the network, so staleness is irrelevant — a stale
        // cached response is the only answer available.
        if (strategy == CacheStrategy.CacheOnly)
        {
            var cacheOnly = await _store.GetCachedResponseAsync(url, ct).ConfigureAwait(false);
            return cacheOnly is not null
                ? BuildResponseFromEnvelope(cacheOnly)
                : HyperwycResponseFactory.CacheMiss(_options.OfflineResponsePolicy);
        }

        // CacheFirst serves a fresh cached response without touching the network.
        // ApiFirst always goes to the network, and consults the cache only on failure.
        if (strategy == CacheStrategy.CacheFirst)
        {
            var cached = await _store.GetCachedResponseAsync(url, ct).ConfigureAwait(false);
            if (cached is not null && !_stalenessEvaluator.IsStale(cached, DateTimeOffset.UtcNow))
                return BuildResponseFromEnvelope(cached);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (strategy == CacheStrategy.ApiFirst)
        {
            // Reachable when IConnectivityService reports online but the API is not
            // actually reachable — a captive portal, DNS failure or transient outage.
            var fallback = await _store.GetCachedResponseAsync(url, ct).ConfigureAwait(false);
            if (fallback is not null)
                return BuildResponseFromEnvelope(fallback);

            throw;
        }

        await CacheResponseIfEligibleAsync(request, response, url, ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Writes a successful read response to the cache, unless its body exceeds
    /// <see cref="HyperwycOptions.MaxCachedResponseBodyBytes"/>. Oversized responses
    /// are still returned to the caller; they are simply not stored.
    /// </summary>
    private async Task CacheResponseIfEligibleAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        string url,
        CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            return;

        // Buffer content so ForCachedResponse can read it synchronously and
        // the caller can still read the body afterwards.
        if (response.Content is not null)
            await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);

        var bodyLength = response.Content?.Headers.ContentLength ?? 0;
        if (bodyLength > _options.MaxCachedResponseBodyBytes)
            return;

        var envelope = Envelope.ForCachedResponse(request, response, _clientName);
        await _store.UpsertAsync(envelope, ct).ConfigureAwait(false);

        _events.Publish(new SyncEvent(
            SyncEventType.OnUpdated, url, request.Method.Method, DateTimeOffset.UtcNow));
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
