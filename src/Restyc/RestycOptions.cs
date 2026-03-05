namespace Restyc;

/// <summary>
/// Configuration options for <see cref="RestycHandler"/> and <see cref="SyncOrchestrator"/>.
/// </summary>
/// <remarks>
/// Extended by later issues (#15 DI extensions, #17 max body size, #20
/// per-endpoint TTL overrides, etc.).
/// </remarks>
public sealed class RestycOptions
{
    /// <summary>
    /// How long a cached response is considered fresh before
    /// <see cref="TtlStalenessEvaluator"/> marks it stale.
    /// Defaults to 5 minutes.
    /// </summary>
    public TimeSpan DefaultCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum response body size (in bytes) that will be written to the cache.
    /// Responses larger than this are returned to the caller but not stored.
    /// Defaults to 524 288 bytes (512 KB).
    /// </summary>
    public int MaxCachedResponseBodyBytes { get; set; } = 512 * 1024;

    /// <summary>
    /// How long to wait after a connectivity-restored event before triggering
    /// a flush, to avoid redundant concurrent flushes during rapid toggling.
    /// Defaults to 2 seconds.
    /// </summary>
    public TimeSpan ConnectivityDebounceDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When <see langword="true"/>, the <see cref="SyncOrchestrator"/> triggers
    /// an outbox flush immediately on startup if the device is currently online.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool FlushOnStartup { get; set; } = true;
}
