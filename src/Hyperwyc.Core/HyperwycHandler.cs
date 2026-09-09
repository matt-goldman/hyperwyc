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
    /// would publish a second <c>OnDelivered</c> and re-run cache invalidation, work
    /// the processor has already taken responsibility for.
    /// </remarks>
    internal static readonly HttpRequestOptionsKey<bool> ReplayMarker = new("Hyperwyc.Replay");

    private static readonly HashSet<HttpMethod> _writeMethods =
    [
        HttpMethod.Post,
        HttpMethod.Put,
        HttpMethod.Patch,
        HttpMethod.Delete,
    ];

    private readonly IHyperwycStore _store;
    private readonly IConnectivityService _connectivity;
    private readonly HyperwycEventStream _events;
    private readonly HyperwycOptions _options;
    private readonly StoreHealth _health;
    private readonly string? _clientName;

    /// <summary>
    /// Initialises a new <see cref="HyperwycHandler"/>.
    /// </summary>
    /// <param name="store">Persistence for queued writes and cached responses.</param>
    /// <param name="connectivity">Reports whether the device is online.</param>
    /// <param name="events">Stream on which sync lifecycle events are published.</param>
    /// <param name="options">Runtime configuration options.</param>
    /// <param name="health">Shared state tracking whether the store can be read.</param>
    /// <param name="clientName">
    /// The name of the <see cref="HttpClient"/> this handler is registered on, stamped
    /// onto queued writes so a replay can be sent back through the same pipeline.
    /// <see langword="null"/> when the handler was registered without a name, in which
    /// case replays fall back to <see cref="HyperwycOptions.ReplayTransport"/>. Use
    /// <c>AddHyperwycHandler()</c> to have this captured automatically.
    /// </param>
    public HyperwycHandler(
        IHyperwycStore store,
        IConnectivityService connectivity,
        HyperwycEventStream events,
        HyperwycOptions options,
        StoreHealth health,
        string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(connectivity);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(health);

        _store = store;
        _connectivity = connectivity;
        _events = events;
        _options = options;
        _health = health;
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

        // Nothing Hyperwyc does is possible without a readable store: it cannot serve a cached
        // response, and it must not accept a write it may be unable to persist. So it steps
        // aside entirely and the request behaves as it would without Hyperwyc installed — which
        // is the truth, and is recoverable, where a 202 promising later delivery would not be.
        if (!_health.IsUsable)
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
        // NetworkOnly is the one strategy that governs writes: the route has declared that a
        // deferred write is the wrong answer, so Hyperwyc does not take custody of it. The
        // request goes to the transport and fails as it would without Hyperwyc installed,
        // which is the truth — accepting it with a 202 would be a promise we were told not to
        // make.
        if (Policy(request).SourcePriority == SourcePriority.NetworkOnly)
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        var queued = await TryQueueWriteAsync(request, ct).ConfigureAwait(false);
        if (queued is not null)
            return queued;

        // Nothing is holding this write, so pass it through rather than answer 202 for a
        // request nobody is keeping.
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists a write to the outbox and builds its <c>202</c>, or returns
    /// <see langword="null"/> if it could not be taken into custody.
    /// </summary>
    /// <remarks>
    /// Shared by both routes into the outbox: a write made while the connectivity service
    /// reports offline, and one whose transport failed before reaching the API. They are the
    /// same situation reached by different means, so they produce the same response.
    /// </remarks>
    private async Task<HttpResponseMessage?> TryQueueWriteAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Buffer content before the synchronous read inside QueuedWrite.For.
        if (request.Content is not null)
        {
            try
            {
                await request.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A body that cannot be read is a write that cannot be replayed.
                return null;
            }
        }

        var write = QueuedWrite.For(request, _clientName);
        if (!await TryStoreAsync(() => _store.UpsertQueuedWriteAsync(write, ct)).ConfigureAwait(false))
            return null;

        _events.Publish(new HyperwycEvent(
            HyperwycEventType.OnQueued,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Method.Method,
            DateTimeOffset.UtcNow,
            CorrelationId: write.CorrelationId,
            RequestId: write.Id,
            RequestBody: write.RequestBody));

        return HyperwycResponseFactory.Queued(write.CorrelationId);
    }

    private Task<HttpResponseMessage> HandleOfflineReadAsync(
        HttpRequestMessage request,
        CancellationToken ct) =>
        ServeReadWithoutNetworkAsync(Policy(request), request.RequestUri?.ToString() ?? string.Empty, ct);

    /// <summary>
    /// Answers a read without the network: the stored response if it is still within its TTL,
    /// otherwise the <c>Offline</c> response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by both routes into the offline state — the connectivity service reporting
    /// offline, and a transport that could not answer. They are the same situation, so a caller
    /// gets the same response either way and cannot tell which occurred.
    /// </para>
    /// <para>
    /// The TTL applies here exactly as it does online: it says how old a stored response may be
    /// and still be served, not when to go looking for a fresher one. Past it, the caller gets
    /// the offline response as though nothing were cached — which is the point, because an
    /// application can act on "no data" and cannot detect "quietly too old".
    /// </para>
    /// </remarks>
    private async Task<HttpResponseMessage> ServeReadWithoutNetworkAsync(
        RoutePolicy policy,
        string url,
        CancellationToken ct)
    {
        // NetworkOnly opts out of the store entirely, so there is nothing to serve.
        if (policy.SourcePriority == SourcePriority.NetworkOnly)
            return HyperwycResponseFactory.Offline();

        var cached = await TryReadAsync(() => _store.GetCachedResponseAsync(url, ct)).ConfigureAwait(false);
        if (cached is not null && !IsStale(cached, policy.Ttl))
            return BuildResponse(cached);

        return HyperwycResponseFactory.Offline();
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
        HttpResponseMessage response;

        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (NeverReachedTheApi(ex)
            && Policy(request).SourcePriority != SourcePriority.NetworkOnly)
        {
            // The connectivity service said online; the transport disagreed, and the transport
            // is the one that knows. No connection was established, so nothing was sent — this
            // is an offline write arrived at by a different route, and it gets the same answer.
            //
            // Without this, a wrong connectivity answer costs the write rather than an attempt,
            // which would make correctness rest on the one thing Hyperwyc cannot verify.
            var queued = await TryQueueWriteAsync(request, ct).ConfigureAwait(false);
            if (queued is not null)
                return queued;

            throw;
        }

        if (response.IsSuccessStatusCode)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;

            if (Policy(request).InvalidateCacheOnWrite)
            {
                var prefix = DeriveInvalidationPrefix(request.RequestUri);
                await TryStoreAsync(() => _store.InvalidateCacheForPrefixAsync(prefix, ct))
                    .ConfigureAwait(false);
            }

            _events.Publish(new HyperwycEvent(
                HyperwycEventType.OnDelivered, url, request.Method.Method, DateTimeOffset.UtcNow));
        }

        return response;
    }

    private async Task<HttpResponseMessage> HandleOnlineReadAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var policy = Policy(request);
        var strategy = policy.SourcePriority;
        var url = request.RequestUri?.ToString() ?? string.Empty;

        // CacheFirst serves a fresh cached response without touching the network.
        // NetworkFirst always goes to the network, and consults the cache only on failure.
        if (strategy == SourcePriority.CacheFirst)
        {
            var cached = await TryReadAsync(() => _store.GetCachedResponseAsync(url, ct)).ConfigureAwait(false);
            if (cached is not null && !IsStale(cached, policy.Ttl))
                return BuildResponse(cached);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The connectivity service said online and no answer came back. Whatever the
            // cause, this read has no network, so it gets the answer a read with no network
            // gets — rather than an exception, which is what the caller would have been
            // spared had the connectivity service happened to be right.
            //
            // Unlike a write, this needs no judgement about how far the request got: a read
            // can always be degraded safely, because both possible answers — a valid cached
            // response, or "no data" — are ones the caller already handles. A write cannot be
            // replayed safely on the same evidence, which is why NeverReachedTheApi guards
            // that path and not this one.
            return await ServeReadWithoutNetworkAsync(policy, url, ct).ConfigureAwait(false);
        }

        // NetworkOnly neither reads nor writes the store, so there is nothing to record.
        if (strategy != SourcePriority.NetworkOnly)
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

        var cached = CachedResponse.For(url, response);
        if (!await TryStoreAsync(() => _store.PutCachedResponseAsync(cached, ct)).ConfigureAwait(false))
            return;

        _events.Publish(new HyperwycEvent(
            HyperwycEventType.OnUpdated, url, request.Method.Method, DateTimeOffset.UtcNow));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Whether a transport failure means the request never left the device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only these four say the connection was never established, so no part of the request was
    /// transmitted and replaying it cannot duplicate anything. They are also the failures a
    /// later network change could plausibly fix, which matters because a connectivity signal is
    /// the only thing that will cause the outbox to be tried again.
    /// </para>
    /// <para>
    /// Deliberately excluded: <see cref="HttpRequestError.InvalidResponse"/>,
    /// <see cref="HttpRequestError.ResponseEnded"/> and
    /// <see cref="HttpRequestError.HttpProtocolError"/> mean a connection was made and the
    /// server may well have processed the request; <see cref="HttpRequestError.Unknown"/>
    /// cannot be reasoned about; and the remainder are client configuration faults that no
    /// amount of connectivity will resolve.
    /// </para>
    /// </remarks>
    private static bool NeverReachedTheApi(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.NameResolutionError
                            or HttpRequestError.ConnectionError
                            or HttpRequestError.SecureConnectionError
                            or HttpRequestError.ProxyTunnelError;

    private static HttpResponseMessage BuildResponse(CachedResponse cached)
    {
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)cached.StatusCode);

        if (cached.Body is not null)
        {
            // ByteArrayContent adds no Content-Type of its own, so the captured response
            // headers below are the only source — which is what keeps an image an image.
            response.Content = new ByteArrayContent(cached.Body);
        }

        foreach (var (key, value) in cached.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(key, value))
                response.Content?.Headers.TryAddWithoutValidation(key, value);
        }

        return response;
    }

    /// <summary>
    /// Runs a store read, degrading to <see langword="null"/> if the store cannot be read.
    /// </summary>
    private async Task<CachedResponse?> TryReadAsync(Func<Task<CachedResponse?>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (StoreHealth.IsStoreFailure(ex))
        {
            _health.ReportUnreadable(ex, _options.UsesDerivedEncryptionKey);
            return null;
        }
    }

    /// <summary>
    /// Runs a store write, reporting and returning <see langword="false"/> if it fails.
    /// </summary>
    private async Task<bool> TryStoreAsync(Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (StoreHealth.IsStoreFailure(ex))
        {
            _health.ReportUnreadable(ex, _options.UsesDerivedEncryptionKey);
            return false;
        }
    }

    /// <summary>The policy for this request, resolved from the route map.</summary>
    private RoutePolicy Policy(HttpRequestMessage request) =>
        _options.Routes.PolicyFor(request.RequestUri);

    /// <summary>Whether a cached response has outlived its route's TTL.</summary>
    /// <remarks>
    /// A comparison, not an abstraction. This was an <c>IStalenessEvaluator</c> with a single
    /// implementation and a single caller, which bought nothing and cost issue #29 — the
    /// evaluator was built from a TTL that configuration had not finished setting. Honouring
    /// <c>Cache-Control</c> (issue #41) is where per-response staleness earns an interface
    /// back; until then it is two lines.
    /// </remarks>
    private static bool IsStale(CachedResponse cached, TimeSpan ttl) =>
        DateTimeOffset.UtcNow - cached.CachedAt > ttl;

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
