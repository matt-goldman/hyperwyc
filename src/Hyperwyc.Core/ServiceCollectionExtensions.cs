using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Hyperwyc.Interfaces;

namespace Hyperwyc;

/// <summary>
/// Extension methods for registering Hyperwyc's core services with a .NET DI container.
/// </summary>
/// <remarks>
/// Most applications should install the <c>Hyperwyc</c> package and call
/// <c>AddHyperwyc()</c>, which supplies durable Cabinet-backed storage with no further
/// configuration. Use the methods here when you are providing your own
/// <see cref="ISyncStore"/>.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Hyperwyc's core services, using <typeparamref name="TStore"/> as the
    /// backing store. The store is constructed by the container, so it may take
    /// constructor dependencies and is disposed by the container if it is disposable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store is a type parameter rather than an option so that omitting it is a
    /// compile-time error. There is no fallback: <see cref="InMemorySyncStore"/> loses
    /// all queued writes and cached responses when the process ends, which is exactly
    /// the failure an offline library exists to prevent, so it is never selected
    /// implicitly.
    /// </para>
    /// <code>
    /// services.AddHyperwycCore&lt;MyCustomStore&gt;(options =&gt;
    /// {
    ///     options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1));
    /// });
    /// </code>
    /// </remarks>
    /// <typeparam name="TStore">
    /// The <see cref="ISyncStore"/> implementation to register. Must be constructible
    /// by the container — supply configuration through its own options type rather
    /// than through primitive constructor parameters, or use the factory overload.
    /// </typeparam>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Optional delegate to configure <see cref="HyperwycOptions"/>.</param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddHyperwycCore<TStore>(
        this IServiceCollection services,
        Action<HyperwycOptions>? configure = null)
        where TStore : class, ISyncStore
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISyncStore, TStore>();
        return AddCoreServices(services, configure);
    }

    /// <summary>
    /// Registers Hyperwyc's core services, using <paramref name="storeFactory"/> to
    /// create the backing store.
    /// </summary>
    /// <remarks>
    /// Use this overload for stores the container cannot construct, or to hand over an
    /// instance you have already built. Note that the container treats a
    /// factory-created singleton as its own, and will dispose it on shutdown if it
    /// implements <see cref="IDisposable"/>.
    /// </remarks>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="storeFactory">Factory producing the <see cref="ISyncStore"/> to use.</param>
    /// <param name="configure">Optional delegate to configure <see cref="HyperwycOptions"/>.</param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddHyperwycCore(
        this IServiceCollection services,
        Func<IServiceProvider, ISyncStore> storeFactory,
        Action<HyperwycOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storeFactory);

        services.TryAddSingleton(storeFactory);
        return AddCoreServices(services, configure);
    }

    // -------------------------------------------------------------------------
    // Shared registration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers everything except the <see cref="ISyncStore"/>, which each public
    /// entry point supplies in its own way.
    /// </summary>
    private static IServiceCollection AddCoreServices(
        IServiceCollection services,
        Action<HyperwycOptions>? configure)
    {
        var options = new HyperwycOptions();
        configure?.Invoke(options);

        // No TTL reconciliation. A resolved RoutePolicy carries its own concrete TTL, so
        // there is nothing to reconcile at registration — which is what issue #29 was: two
        // sources for one value, resolved in the wrong order.

        // Register the options object itself as a singleton so HyperwycHandler
        // and SyncOrchestrator can receive it via constructor injection.
        services.AddSingleton(options);

        // Connectivity has no default on purpose: see HyperwycOptions.Connectivity.
        //
        // Registering an IConnectivityService in the container is the expected route, so this
        // must not care whether that registration comes before or after AddHyperwyc — a
        // consumer choosing the order, or a source generator wiring it up, should both work.
        // Hence a placeholder that throws when resolved rather than a check here: TryAdd
        // stands aside for an earlier registration, and a later one wins because the last
        // descriptor for a service is the one the container resolves. The failure lands on
        // first use instead of at startup, which is the price of not being order-dependent.
        if (options.Connectivity is { } connectivity)
            services.TryAddSingleton(_ => connectivity);
        else
            services.TryAddSingleton<IConnectivityService>(
                _ => throw new InvalidOperationException(NoConnectivityMessage));

        // Core singletons.
        services.TryAddSingleton<SyncEventStream>();

        services.TryAddSingleton<IHyperwyc>(sp => new HyperwycService(
            sp.GetRequiredService<SyncEventStream>(),
            sp.GetRequiredService<SyncOrchestrator>()));

        // Replays go back through the named client they were queued on, so downstream
        // handlers (auth above all) apply to them. ReplayTransport is the fallback for
        // envelopes with no client name — see issue #37.
        services.TryAddSingleton<SyncOrchestrator>(sp => new SyncOrchestrator(
            sp.GetRequiredService<ISyncStore>(),
            sp.GetRequiredService<IConnectivityService>(),
            sp.GetRequiredService<SyncEventStream>(),
            sp.GetRequiredService<HyperwycOptions>(),
            options.ReplayTransport ?? new HttpClientHandler(),
            sp.GetService<IHttpClientFactory>()));

        // HyperwycHandler is transient — each named HTTP client pipeline gets its own instance.
        services.TryAddTransient<HyperwycHandler>();

        // Startup flush hosted service.
        if (options.FlushOnStartup)
            services.AddHostedService<HyperwycHostedService>();

        return services;
    }

    private const string NoConnectivityMessage =
        """
        Hyperwyc needs an IConnectivityService and deliberately has no default, because
        detecting connectivity depends on the platform — something only your application
        knows. Defaulting to "always online" would leave Hyperwyc caching responses while
        never queueing or replaying anything, which looks like it is working and is not.

        Register your implementation in the container:

            services.AddSingleton<IConnectivityService, MyConnectivityService>();

        Before or after AddHyperwyc; either order works. Alternatively, set
        HyperwycOptions.Connectivity when you register Hyperwyc.

        If you do not have an implementation yet:

          - On .NET MAUI, one over Connectivity.Current is about twenty lines. The sample
            application has one to copy.

          - NetworkAvailabilityConnectivityService ships with Hyperwyc. BCL-only, no platform
            dependency. Detects a hard-offline device, but reports "connected" behind a
            captive portal or when the network is up and your API is not.

          - AlwaysOnlineConnectivityService, if this host is genuinely always connected or you
            only want the response cache. Nothing is ever queued or replayed under it.
        """;
}
