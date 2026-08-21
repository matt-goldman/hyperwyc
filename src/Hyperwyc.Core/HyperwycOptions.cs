using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Configuration options for <see cref="HyperwycHandler"/> and <see cref="SyncOrchestrator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Set properties on this class and pass a configure delegate to
/// <c>AddHyperwyc(options =&gt; ...)</c> (or
/// <see cref="ServiceCollectionExtensions.AddHyperwycCore{TStore}"/>) to register
/// Hyperwyc with a .NET DI container.
/// </para>
/// <para>
/// The <see cref="ISyncStore"/> is deliberately absent from this class. It is supplied
/// as a type parameter to <c>AddHyperwycCore&lt;TStore&gt;()</c> so that a missing store
/// is a compile-time error rather than a silent fall back to non-durable storage.
/// </para>
/// </remarks>
public sealed class HyperwycOptions
{
    // -------------------------------------------------------------------------
    // Core policy / infrastructure
    // -------------------------------------------------------------------------

    /// <summary>
    /// The default caching and sync policy applied to all requests. Defaults to
    /// <see cref="SyncPolicy.CacheFirst()"/>, which takes its freshness window
    /// from <see cref="DefaultCacheTtl"/>.
    /// </summary>
    /// <remarks>
    /// Assigning <see cref="SyncPolicy.CacheFirst(TimeSpan)"/> states the TTL on
    /// the policy, which takes precedence over <see cref="DefaultCacheTtl"/>. The
    /// default policy deliberately carries no TTL of its own, so that setting
    /// <see cref="DefaultCacheTtl"/> alone is honoured.
    /// </remarks>
    public ISyncPolicy DefaultPolicy { get; set; } = SyncPolicy.CacheFirst();

    /// <summary>
    /// Reports whether the device can reach the network. <b>Required</b>, though most
    /// applications satisfy it by registering an <see cref="IConnectivityService"/> in the
    /// container rather than by setting this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The usual route is a plain container registration, in either order relative to
    /// <c>AddHyperwyc</c>:
    /// </para>
    /// <code>
    /// services.AddSingleton&lt;IConnectivityService, MyConnectivityService&gt;();
    /// </code>
    /// <para>
    /// This property is the alternative for an instance you already hold, or when you would
    /// rather keep the configuration in one place. A container registration wins if both are
    /// present. If neither is, resolving <see cref="IConnectivityService"/> throws with a
    /// message describing the options.
    /// </para>
    /// <para>
    /// Deliberately has no default. Hyperwyc can choose a store for you because any durable
    /// store will do, but it cannot choose a connectivity source: that depends on the
    /// platform, which is something only your application knows. Silently defaulting to
    /// "always online" would leave the library caching responses while never queueing or
    /// replaying anything — working in appearance and not in substance, which is the exact
    /// failure it exists to prevent.
    /// </para>
    /// <para>
    /// If you have no implementation yet, there are three answers: one written against your
    /// platform (about twenty lines on .NET MAUI, and the sample application has one to
    /// copy); <see cref="NetworkAvailabilityConnectivityService"/>, which is BCL-only and
    /// detects hard-offline but not a captive portal; or
    /// <see cref="AlwaysOnlineConnectivityService"/>, under which nothing is ever queued or
    /// replayed.
    /// </para>
    /// </remarks>
    public IConnectivityService? Connectivity { get; set; }

    /// <summary>
    /// The transport used to replay queued writes from the outbox. Leave
    /// <see langword="null"/> to use a default <see cref="HttpClientHandler"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replayed requests do not travel through your application's
    /// <see cref="HttpClient"/> pipeline — they are sent directly, bypassing
    /// <see cref="HyperwycHandler"/> so that a replay is not queued again. That
    /// means handler-level concerns you rely on for ordinary requests (certificate
    /// pinning, proxies, timeouts, logging) do not reach replays unless you supply
    /// a transport that includes them here.
    /// </para>
    /// <para>
    /// Supplying a stub is also how you exercise a flush in tests without network
    /// access.
    /// </para>
    /// <para>
    /// <b>Ownership:</b> Hyperwyc never disposes this handler, whether you supplied
    /// it or it defaulted. A handler you provide remains yours to dispose. The
    /// default is created once and lives for the lifetime of the application.
    /// </para>
    /// </remarks>
    public HttpMessageHandler? ReplayTransport { get; set; }

    /// <summary>
    /// Determines whether a cached response is still fresh. Leave
    /// <see langword="null"/> to use a <see cref="TtlStalenessEvaluator"/> built
    /// from the effective TTL.
    /// </summary>
    /// <remarks>
    /// This is <see langword="null"/> until registration precisely so that the
    /// default can be constructed <em>after</em> the effective TTL is known.
    /// Building it eagerly here would capture the TTL before
    /// <see cref="DefaultPolicy"/> and <see cref="DefaultCacheTtl"/> had been
    /// configured, which is the defect issue #29 records.
    /// </remarks>
    public IStalenessEvaluator? StalenessEvaluator { get; set; }

    // -------------------------------------------------------------------------
    // Cache settings
    // -------------------------------------------------------------------------

    /// <summary>
    /// How long a cached response is considered fresh before
    /// <see cref="TtlStalenessEvaluator"/> marks it stale.
    /// Defaults to 5 minutes.
    /// </summary>
    /// <remarks>
    /// A TTL supplied to <see cref="SyncPolicy.CacheFirst(TimeSpan)"/> wins over
    /// this value. After registration this property holds the effective TTL,
    /// whichever source it came from.
    /// </remarks>
    public TimeSpan DefaultCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Controls the HTTP status code returned by Hyperwyc when it handles a
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
