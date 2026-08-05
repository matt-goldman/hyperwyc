namespace Hyperwyc;

/// <summary>
/// Defines the strategy used to resolve a request — whether to prefer a cached
/// response, always hit the network, or fall back gracefully when offline.
/// </summary>
public enum CacheStrategy
{
    /// <summary>
    /// Return a cached response when available; only call the API if the cache
    /// is empty or stale.
    /// </summary>
    CacheFirst,

    /// <summary>
    /// Always call the API first; fall back to cache only when the network is
    /// unavailable.
    /// </summary>
    ApiFirst,

    /// <summary>
    /// Only ever return a cached response. Never make a network request.
    /// </summary>
    CacheOnly,

    /// <summary>
    /// Always call the API. Never read from or write to the cache.
    /// </summary>
    NetworkOnly,
}
