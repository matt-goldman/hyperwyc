namespace Hyperwyc;

/// <summary>
/// Defines the strategy used to resolve a request — whether to prefer a cached
/// response, always hit the network, or fall back gracefully when offline.
/// </summary>
public enum SourcePriority
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
    NetworkFirst,


    /// <summary>
    /// Never involve Hyperwyc's store, in either direction. Reads are neither served from it
    /// nor written to it, and <b>writes are not queued when offline</b> — they go to the
    /// transport and fail as they would without Hyperwyc installed.
    /// </summary>
    /// <remarks>
    /// The only strategy that governs writes as well as reads. The others leave writes at the
    /// default of queueing when offline.
    /// </remarks>
    NetworkOnly,
}
