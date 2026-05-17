using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Hyperwyc.Interfaces;

namespace Hyperwyc;

/// <summary>
/// Extension methods for registering Hyperwyc with a .NET DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Hyperwyc services with the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use the returned <see cref="IServiceCollection"/> to add Hyperwyc handlers
    /// to named HTTP clients:
    /// </para>
    /// <code>
    /// services.AddHttpClient("MyApi")
    ///     .AddHttpMessageHandler&lt;HyperwycHandler&gt;();
    ///
    /// services.AddHyperwyc(options =>
    /// {
    ///     options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1));
    ///     options.Store = new CabinetSyncStore("Hyperwyc.db");
    ///     options.Connectivity = new MauiConnectivityService();
    /// });
    /// </code>
    /// </remarks>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Optional delegate to configure <see cref="HyperwycOptions"/>.</param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddHyperwyc(
        this IServiceCollection services,
        Action<HyperwycOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new HyperwycOptions();
        configure?.Invoke(options);

        // If the default policy carries a TTL, propagate it to DefaultCacheTtl
        // so TtlStalenessEvaluator and other consumers stay in sync.
        if (options.DefaultPolicy is SyncPolicy.PresetSyncPolicy { Ttl: { } policyTtl })
            options.DefaultCacheTtl = policyTtl;

        // Register the options object itself as a singleton so HyperwycHandler
        // and SyncOrchestrator can receive it via constructor injection.
        services.AddSingleton(options);

        // Interfaces resolved from the options instance.
        services.TryAddSingleton<ISyncStore>(_ => options.Store);
        services.TryAddSingleton<IConnectivityService>(_ => options.Connectivity);
        services.TryAddSingleton<ISyncPolicy>(_ => options.DefaultPolicy);
        services.TryAddSingleton<IStalenessEvaluator>(_ => options.StalenessEvaluator);

        // Core singletons.
        services.TryAddSingleton<SyncEventStream>();

        services.TryAddSingleton<IHyperwyc>(sp => new HyperwycService(
            sp.GetRequiredService<SyncEventStream>(),
            sp.GetRequiredService<ISyncStore>()));

        services.TryAddSingleton<SyncOrchestrator>(sp => new SyncOrchestrator(
            sp.GetRequiredService<ISyncStore>(),
            sp.GetRequiredService<ISyncPolicy>(),
            sp.GetRequiredService<IConnectivityService>(),
            sp.GetRequiredService<SyncEventStream>(),
            sp.GetRequiredService<HyperwycOptions>(),
            new HttpClientHandler()));

        // HyperwycHandler is transient — each named HTTP client pipeline gets its own instance.
        services.TryAddTransient<HyperwycHandler>();

        // Startup flush hosted service.
        if (options.FlushOnStartup)
            services.AddHostedService<HyperwycHostedService>();

        return services;
    }
}
