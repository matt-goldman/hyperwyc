using Hyperwyc.Interfaces;

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
    /// Per-route policies, matched first-registered-wins, plus the default for routes matching
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Register most specific first — see <see cref="RoutePolicyMap"/>. The matched policy is
    /// the single source of cache strategy, TTL and write-invalidation; there is no second
    /// place a TTL can come from, which is what caused issue #29.
    /// </remarks>
    public RoutePolicyMap Routes { get; } = new();

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


    // -------------------------------------------------------------------------
    // Cache settings
    // -------------------------------------------------------------------------



    /// <summary>
    /// Maximum response body size (in bytes) that will be written to the cache.
    /// Responses larger than this are returned to the caller but not stored.
    /// Defaults to 524 288 bytes (512 KB).
    /// </summary>
    public int MaxCachedResponseBodyBytes { get; set; } = 512 * 1024;

    /// <summary>
    /// Maximum response body size (in bytes) captured onto a <see cref="Models.SyncOutcome"/>
    /// when a queued write is delivered or rejected. Longer bodies are clipped and flagged
    /// with <see cref="Models.SyncOutcome.BodyTruncated"/>. Defaults to 16 384 bytes (16 KB).
    /// Set to zero to capture no bodies at all.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from — and far smaller than — <see cref="MaxCachedResponseBodyBytes"/>.
    /// That one sizes a payload being cached for later reads; this one sizes an explanation of
    /// why a write failed, which is persisted per dead-lettered envelope and is usually a
    /// sentence. Clipping rather than dropping because half an error message is still
    /// actionable and a missing one is not.
    /// </remarks>
    public int MaxOutcomeBodyBytes { get; set; } = 16 * 1024;

    // -------------------------------------------------------------------------
    // Orchestrator settings
    // -------------------------------------------------------------------------


    /// <summary>
    /// When <see langword="true"/>, the <see cref="SyncOrchestrator"/> triggers
    /// an outbox flush immediately on startup if the device is currently online.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool FlushOnStartup { get; set; } = true;

}
