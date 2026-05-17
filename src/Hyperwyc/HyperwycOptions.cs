using hyperwyc.Interfaces;
using hyperwyc.Models;

namespace hyperwyc;

/// <summary>
/// Configuration options for <see cref="hyperwycHandler"/> and <see cref="SyncOrchestrator"/>.
/// </summary>
/// <remarks>
/// Extended by later issues (#17 max body size, #20 per-endpoint TTL overrides, etc.).
/// Set properties on this class and call <c>Addhyperwyc(options => ...)</c> to
/// register hyperwyc with a .NET DI container.
/// </remarks>
public sealed class hyperwycOptions
{
    // -------------------------------------------------------------------------
    // Core policy / infrastructure
    // -------------------------------------------------------------------------

    /// <summary>
    /// The default caching and sync policy applied to all requests unless
    /// overridden per-endpoint. Defaults to <see cref="SyncPolicy.CacheFirst"/>
    /// with a 1-day TTL.
    /// </summary>
    public ISyncPolicy DefaultPolicy { get; set; } =
        SyncPolicy.CacheFirst(TimeSpan.FromDays(1));

    /// <summary>
    /// The backing store for outbox entries and cached responses.
    /// Defaults to <see cref="InMemorySyncStore"/>; replace with
    /// <c>CabinetSyncStore</c> for durable persistence.
    /// </summary>
    public ISyncStore Store { get; set; } = new InMemorySyncStore();

    /// <summary>
    /// Provides network reachability information. Defaults to
    /// <see cref="AlwaysOnlineConnectivityService"/>; replace with a
    /// platform-specific implementation (e.g. <c>MauiConnectivityService</c>).
    /// </summary>
    public IConnectivityService Connectivity { get; set; } =
        new AlwaysOnlineConnectivityService();

    /// <summary>
    /// Determines whether a cached response is still fresh. Defaults to
    /// <see cref="TtlStalenessEvaluator"/> using <see cref="DefaultCacheTtl"/>.
    /// </summary>
    public IStalenessEvaluator StalenessEvaluator { get; set; } =
        new TtlStalenessEvaluator(TimeSpan.FromMinutes(5));

    // -------------------------------------------------------------------------
    // Cache settings
    // -------------------------------------------------------------------------

    /// <summary>
    /// How long a cached response is considered fresh before
    /// <see cref="TtlStalenessEvaluator"/> marks it stale.
    /// Defaults to 5 minutes.
    /// </summary>
    public TimeSpan DefaultCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Controls the HTTP status code returned by hyperwyc when it handles a
    /// request offline.  Defaults to <see cref="OfflineResponsePolicy.Transparent"/>
    /// (200 OK) so app code never needs to branch on connectivity status —
    /// matching the Service Worker pattern used in progressive web apps.
    /// </summary>
    public OfflineResponsePolicy OfflineResponsePolicy { get; set; } =
        OfflineResponsePolicy.Transparent;

    /// <summary>
    /// Maximum response body size (in bytes) that will be written to the cache.
    /// Responses larger than this are returned to the caller but not stored.
    /// Defaults to 524 288 bytes (512 KB).
    /// </summary>
    public int MaxCachedResponseBodyBytes { get; set; } = 512 * 1024;

    // -------------------------------------------------------------------------
    // Orchestrator settings
    // -------------------------------------------------------------------------

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

    /// <summary>
    /// Default retry configuration applied to outbox entries when
    /// <see cref="ISyncPolicy.GetRetryOptions"/> is not otherwise overridden.
    /// Defaults to 5 retries with 2-second initial delay and exponential backoff.
    /// </summary>
    public RetryOptions DefaultRetryOptions { get; set; } =
        new(MaxRetries: 5, InitialDelay: TimeSpan.FromSeconds(2), BackoffMultiplier: 2.0);
}
