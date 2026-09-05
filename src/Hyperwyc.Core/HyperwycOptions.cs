using Hyperwyc.Interfaces;

namespace Hyperwyc;

/// <summary>
/// Configuration options for <see cref="HyperwycHandler"/> and <see cref="OutboxProcessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Set properties on this class and pass a configure delegate to
/// <c>AddHyperwyc(options =&gt; ...)</c> (or
/// <see cref="ServiceCollectionExtensions.AddHyperwycCore{TStore}"/>) to register
/// Hyperwyc with a .NET DI container.
/// </para>
/// <para>
/// The <see cref="IHyperwycStore"/> is deliberately absent from this class. It is supplied
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
    /// Reports whether the device can reach the network. Optional — most applications supply
    /// one by registering an <see cref="IConnectivityService"/> in the container, and one that
    /// supplies none gets <see cref="NetworkAvailabilityConnectivityService"/>.
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
    /// present.
    /// </para>
    /// <para>
    /// <b>Supply one anyway on a mobile device.</b> The fallback reports whether a network
    /// interface is up, not whether your API is reachable, so it says "connected" behind a
    /// captive portal or on a signal too weak to carry a request. That is the harmless
    /// direction — the request is attempted, the transport fails, and Hyperwyc degrades exactly
    /// as if the device had been known to be offline — but each wrong answer costs a doomed
    /// request first. An implementation over <c>Connectivity.Current</c> is about twenty lines
    /// and the sample application has one to copy.
    /// </para>
    /// <para>
    /// <b>Which way an implementation errs matters more than how often.</b> Reporting online
    /// while offline is self-correcting, because the transport is consulted and contradicts it.
    /// Reporting offline while online is not: Hyperwyc does not touch the transport, so nothing
    /// contradicts it. Nothing is lost either way, but the second costs freshness and delays a
    /// write until the next connectivity change — and an implementation stuck reporting offline
    /// never raises one. Prefer erring toward connected. See ADR 0007.
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
    /// Maximum response body size (in bytes) captured onto a <see cref="Models.DeliveryOutcome"/>
    /// when a queued write is delivered or rejected. Longer bodies are clipped and flagged
    /// with <see cref="Models.DeliveryOutcome.BodyTruncated"/>. Defaults to 16 384 bytes (16 KB).
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
    /// When <see langword="true"/>, the <see cref="OutboxProcessor"/> triggers
    /// an outbox flush immediately on startup if the device is currently online.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool FlushOnStartup { get; set; } = true;

    /// <summary>
    /// Whether the store's encryption key was derived by Hyperwyc rather than supplied by the
    /// consumer. Set by the storage package; not something to configure.
    /// </summary>
    /// <remarks>
    /// Affects nothing but the wording of one log line, when the store turns out to be
    /// unreadable (issue #49). A derived key that no longer matches usually means the store
    /// directory moved or its persisted shape changed; a supplied key that does not match is a
    /// different conversation. The behaviour is identical either way — neither case entitles
    /// Hyperwyc to destroy anything.
    /// </remarks>
    public bool UsesDerivedEncryptionKey { get; set; }

}
