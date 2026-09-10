using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// <see cref="IHyperwycStore"/>.
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
    /// compile-time error. There is no fallback: <see cref="InMemoryStore"/> loses
    /// all queued writes and cached responses when the process ends, which is exactly
    /// the failure an offline library exists to prevent, so it is never selected
    /// implicitly.
    /// </para>
    /// <code>
    /// services.AddHyperwycCore&lt;MyCustomStore&gt;(options =&gt;
    /// {
    ///     options.Routes.Default = RoutePolicy.CacheFirst(TimeSpan.FromDays(1));
    /// });
    /// </code>
    /// </remarks>
    /// <typeparam name="TStore">
    /// The <see cref="IHyperwycStore"/> implementation to register. Must be constructible
    /// by the container — supply configuration through its own options type rather
    /// than through primitive constructor parameters, or use the factory overload.
    /// <para>
    /// The <see cref="DynamicallyAccessedMembersAttribute"/> is what tells the trimmer the
    /// container will reach for this type's constructors reflectively, so they are kept. It
    /// also propagates: a caller passing a store here must be somewhere the trimmer can see
    /// the concrete type, which it can, because the type argument is written at the call site.
    /// </para>
    /// </typeparam>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Optional delegate to configure <see cref="HyperwycOptions"/>.</param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddHyperwycCore<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this IServiceCollection services,
        Action<HyperwycOptions>? configure = null)
        where TStore : class, IHyperwycStore
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IHyperwycStore, TStore>();
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
    /// <param name="storeFactory">Factory producing the <see cref="IHyperwycStore"/> to use.</param>
    /// <param name="configure">Optional delegate to configure <see cref="HyperwycOptions"/>.</param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddHyperwycCore(
        this IServiceCollection services,
        Func<IServiceProvider, IHyperwycStore> storeFactory,
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
    /// Registers everything except the <see cref="IHyperwycStore"/>, which each public
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
        // and OutboxProcessor can receive it via constructor injection.
        services.AddSingleton(options);

        // Connectivity falls back to NetworkAvailabilityConnectivityService when nothing is
        // supplied. That is a real default rather than a guess dressed as one, because the way
        // this implementation errs is the recoverable way: it reports connected when it should
        // not, the transport is consulted, it fails, and Hyperwyc degrades on what the
        // transport said. The unrecoverable direction — reporting offline while online, which
        // nothing contradicts because no request is made — is one GetIsNetworkAvailable()
        // essentially cannot produce on a working network. See ADR 0007.
        //
        // Registering an IConnectivityService in the container is still the expected route, and
        // must work whether that registration comes before or after AddHyperwyc — a consumer
        // choosing the order, or a source generator wiring it up, should both work. TryAdd
        // stands aside for an earlier registration, and a later one wins because the last
        // descriptor for a service is the one the container resolves.
        if (options.Connectivity is { } connectivity)
            services.TryAddSingleton(_ => connectivity);
        else
            services.TryAddSingleton<IConnectivityService>(sp =>
            {
                // Said once, at the moment the fallback is actually taken. A consumer paying
                // avoidable timeouts on mobile needs something that lets them attribute it;
                // this is not a warning that anything is broken.
                sp.GetService<ILoggerFactory>()
                  ?.CreateLogger("Hyperwyc.Connectivity")
                  .LogInformation("{Message}", DefaultConnectivityMessage);

                return new NetworkAvailabilityConnectivityService();
            });

        // Core singletons.
        services.TryAddSingleton<HyperwycEventStream>();

        // Shared because the handler is transient and the processor is a singleton, and they
        // have to agree about whether the store can be read. Logging is optional: Hyperwyc must
        // work in a container that has none.
        services.TryAddSingleton(sp => new StoreHealth(
            sp.GetRequiredService<HyperwycEventStream>(),
            sp.GetService<ILogger<StoreHealth>>()));

        // HyperwycService is the single implementation behind both consumer interfaces.
        // Registered as itself so both interface aliases resolve to the same instance —
        // resolving one via a factory that news up another would give two, and each would
        // hold its own view of what the outbox looks like.
        services.TryAddSingleton<HyperwycService>(sp => new HyperwycService(
            sp.GetRequiredService<HyperwycEventStream>(),
            sp.GetRequiredService<OutboxProcessor>()));
        services.TryAddSingleton<IHyperwyc>(sp => sp.GetRequiredService<HyperwycService>());
        services.TryAddSingleton<IHyperwycDiagnostics>(sp => sp.GetRequiredService<HyperwycService>());

        // Replays go back through the named client they were queued on, so downstream
        // handlers (auth above all) apply to them. ReplayTransport is the fallback for
        // envelopes with no client name — see issue #37.
        services.TryAddSingleton<OutboxProcessor>(sp => new OutboxProcessor(
            sp.GetRequiredService<IHyperwycStore>(),
            sp.GetRequiredService<IConnectivityService>(),
            sp.GetRequiredService<HyperwycEventStream>(),
            sp.GetRequiredService<HyperwycOptions>(),
            options.ReplayTransport ?? new HttpClientHandler(),
            sp.GetRequiredService<StoreHealth>(),
            sp.GetService<IHttpClientFactory>()));

        // HyperwycHandler is transient — each named HTTP client pipeline gets its own instance.
        services.TryAddTransient<HyperwycHandler>();

        // Startup flush hosted service.
        if (options.FlushOnStartup)
            services.AddHostedService<HyperwycHostedService>();

        return services;
    }

    private const string DefaultConnectivityMessage =
        """
        No IConnectivityService was registered, so Hyperwyc is using
        NetworkAvailabilityConnectivityService. It reports whether a network interface is up,
        not whether your API is reachable, so it reports "connected" behind a captive portal,
        on a router with no upstream, or on a signal too weak to carry a request.

        It errs toward reporting connected, which is the harmless direction: the request is
        attempted, the transport fails, and a read is served from the store while a write is
        queued, exactly as if the device had been known to be offline. Every wrong answer still
        costs a doomed request first, which on a mobile device is worth avoiding:

          - On .NET MAUI, an implementation over Connectivity.Current is about twenty lines and
            the sample application has one to copy.

          - On Windows outside MAUI, NetworkInformation.GetInternetConnectionProfile reports
            reachability rather than link state.

        Register one and this message stops:

            services.AddSingleton<IConnectivityService, MyConnectivityService>();

        Before or after AddHyperwyc; either order works. Alternatively, set
        HyperwycOptions.Connectivity when you register Hyperwyc.
        """;
}
