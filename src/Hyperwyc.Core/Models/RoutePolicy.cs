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
/// Policies are matched to routes by <see cref="RoutePolicyMap"/>, first match wins.
/// </para>
/// </remarks>
public sealed record RoutePolicy
{
    /// <summary>
    /// How reads are served, and — for <see cref="CacheStrategy.NetworkOnly"/> — whether writes
    /// are queued at all.
    /// </summary>
    public CacheStrategy Strategy { get; init; } = CacheStrategy.CacheFirst;

    /// <summary>
    /// How long a stored response stays fresh. Defaults to 5 minutes.
    /// </summary>
    /// <remarks>
    /// Always concrete, never inherited. A TTL that could come from two places is what caused
    /// issue #29, so the matched policy is the single source and there is no fallback chain.
    /// </remarks>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(5);

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

    /// <summary>Serve a fresh stored response without touching the network; otherwise fetch.</summary>
    public static RoutePolicy CacheFirst() => new();

    /// <summary>Serve a stored response for <paramref name="ttl"/>; otherwise fetch.</summary>
    public static RoutePolicy CacheFirst(TimeSpan ttl) => new() { Ttl = ttl };

    /// <summary>Always try the network first, falling back to a stored response on failure.</summary>
    public static RoutePolicy NetworkFirst() =>
        new() { Strategy = CacheStrategy.NetworkFirst };

    /// <inheritdoc cref="NetworkFirst()"/>
    public static RoutePolicy NetworkFirst(TimeSpan ttl) =>
        new() { Strategy = CacheStrategy.NetworkFirst, Ttl = ttl };


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
        new() { Strategy = CacheStrategy.NetworkOnly, InvalidateCacheOnWrite = false };
}
