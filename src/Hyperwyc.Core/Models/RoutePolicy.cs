namespace Hyperwyc.Models;

/// <summary>
/// How Hyperwyc treats one route: whether it consults its store, how long a stored response
/// stays fresh, and whether a write clears what it has.
/// </summary>
/// <remarks>
/// <para>
/// A policy is a value, not a function of a request. Deciding per request is not a policy — it
/// is a handler, and a consumer who wants that has their own <see cref="System.Net.Http.DelegatingHandler"/>.
/// That is why this replaced <c>ISyncPolicy</c>, whose members both took an
/// <see cref="HttpRequestMessage"/> that no implementation ever used.
/// </para>
/// <para>
/// Policies are matched to routes by <see cref="RoutePolicyMap"/>: registered from general
/// to specific, with the last matching registration winning.
/// </para>
/// </remarks>
public sealed record RoutePolicy
{
    /// <summary>
    /// How reads are served, and — for <see cref="SourcePriority.NetworkOnly"/> — whether writes
    /// are queued at all.
    /// </summary>
    public SourcePriority SourcePriority { get; init; } = SourcePriority.CacheFirst;

    /// <summary>
    /// How old a stored response may be and still be served. Defaults to one day.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A validity bound, not a refetch trigger.</b> Past its TTL a stored response is not
    /// served at all — online it is refetched, offline the caller gets the synthetic offline
    /// response as though nothing were cached. The TTL is the whole of the answer to "how stale
    /// may this be", and it means the same thing whether or not there is a network.
    /// </para>
    /// <para>
    /// So set it to how long the data is genuinely useful, not to how often you would like to
    /// refresh. "Always fetch when I can" is <see cref="NetworkFirst()"/>, which is a strategy;
    /// using a short TTL to force refetching would leave nothing servable offline, which is
    /// the opposite of what this library is for.
    /// </para>
    /// <para>
    /// Always concrete, never inherited. A TTL that could come from two places is what caused
    /// issue #29, so the matched policy is the single source and there is no fallback chain.
    /// </para>
    /// </remarks>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether a successful write clears stored responses under the same path prefix.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Governs the route that was written to, not relationships between routes. Declaring that
    /// a write to one route invalidates another is domain knowledge Hyperwyc does not have —
    /// see issue #22.
    /// </remarks>
    public bool InvalidateCacheOnWrite { get; init; } = true;

    /// <summary>
    /// Maximum response body size (in bytes) written to the cache on this route.
    /// <see langword="null"/> — the default — defers to
    /// <see cref="HyperwycOptions.MaxCachedResponseBodyBytes"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The global cap is one number for an application whose routes return payloads of very
    /// different sizes: raise it for a route that returns a document and every other route
    /// gains headroom it never needed. This is the per-route escape hatch — set it where one
    /// route's payloads justify a different bound, and leave the rest on the global default.
    /// </para>
    /// <para>
    /// Unlike <see cref="Ttl"/> this <em>is</em> inherited when unset, and deliberately so. A TTL
    /// has no safe fallback — issue #29 was two sources for one validity bound — whereas a size
    /// cap has exactly one: the application-wide number the consumer already chose. Making it
    /// concrete on every policy would mean restating 512 KB on each route that does not care.
    /// </para>
    /// <para>
    /// Zero caches no bodies at all on this route. There is no "unlimited" sentinel because none
    /// is needed: a body is a <see cref="byte"/> array, so <see cref="int.MaxValue"/> is already
    /// larger than anything that could be cached. Negative values throw at configuration time.
    /// </para>
    /// </remarks>
    public int? MaxCachedResponseBodyBytes { get; init; }

    /// <summary>Serve a fresh stored response without touching the network; otherwise fetch.</summary>
    public static RoutePolicy CacheFirst() => new();

    /// <summary>Serve a stored response for <paramref name="ttl"/>; otherwise fetch.</summary>
    public static RoutePolicy CacheFirst(TimeSpan ttl) => new() { Ttl = ttl };

    /// <summary>Always try the network first, falling back to a stored response on failure.</summary>
    public static RoutePolicy NetworkFirst() =>
        new() { SourcePriority = SourcePriority.NetworkFirst };

    /// <inheritdoc cref="NetworkFirst()"/>
    public static RoutePolicy NetworkFirst(TimeSpan ttl) =>
        new() { SourcePriority = SourcePriority.NetworkFirst, Ttl = ttl };


    /// <summary>
    /// Never involve Hyperwyc's store on this route, in either direction: reads are not served
    /// from it or written to it, and <b>writes are not queued when offline</b> — they go to the
    /// transport and fail as they would without Hyperwyc installed.
    /// </summary>
    /// <remarks>
    /// The one strategy that governs writes as well as reads. Use it where deferring a write is
    /// the wrong answer even though deferring a read would be fine — a payment, a seat
    /// reservation, anything contending for a shared mutable resource. Hyperwyc declining to
    /// take custody is more honest than accepting a write it may deliver hours later.
    /// </remarks>
    public static RoutePolicy NetworkOnly() =>
        new() { SourcePriority = SourcePriority.NetworkOnly, InvalidateCacheOnWrite = false };
}
