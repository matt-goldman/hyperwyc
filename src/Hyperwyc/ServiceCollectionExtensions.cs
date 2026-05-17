using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using hyperwyc.Interfaces;

namespace hyperwyc;

/// <summary>
/// Extension methods for registering hyperwyc with a .NET DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers hyperwyc services with the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use the returned <see cref="IServiceCollection"/> to add hyperwyc handlers
    /// to named HTTP clients:
    /// </para>
    /// <code>
    /// services.AddHttpClient("MyApi")
    ///     .AddHttpMessageHandler&lt;hyperwycHandler&gt;();
    ///
    /// services.Addhyperwyc(options =>
    /// {
    ///     options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1));
    ///     options.Store = new CabinetSyncStore("hyperwyc.db");
    ///     options.Connectivity = new MauiConnectivityService();
    /// });
    /// </code>
    /// </remarks>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Optional delegate to configure <see cref="hyperwycOptions"/>.</param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection Addhyperwyc(
        this IServiceCollection services,
        Action<hyperwycOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new hyperwycOptions();
        configure?.Invoke(options);

        // If the default policy carries a TTL, propagate it to DefaultCacheTtl
        // so TtlStalenessEvaluator and other consumers stay in sync.
        if (options.DefaultPolicy is SyncPolicy.PresetSyncPolicy { Ttl: { } policyTtl })
            options.DefaultCacheTtl = policyTtl;

        // Register the options object itself as a singleton so hyperwycHandler
        // and SyncOrchestrator can receive it via constructor injection.
        services.AddSingleton(options);

        // Interfaces resolved from the options instance.
        services.TryAddSingleton<ISyncStore>(_ => options.Store);
        services.TryAddSingleton<IConnectivityService>(_ => options.Connectivity);
        services.TryAddSingleton<ISyncPolicy>(_ => options.DefaultPolicy);
        services.TryAddSingleton<IStalenessEvaluator>(_ => options.StalenessEvaluator);

        // Core singletons.
        services.TryAddSingleton<SyncEventStream>();

        services.TryAddSingleton<Ihyperwyc>(sp => new hyperwycService(
            sp.GetRequiredService<SyncEventStream>(),
            sp.GetRequiredService<ISyncStore>()));

        services.TryAddSingleton<SyncOrchestrator>(sp => new SyncOrchestrator(
            sp.GetRequiredService<ISyncStore>(),
            sp.GetRequiredService<ISyncPolicy>(),
            sp.GetRequiredService<IConnectivityService>(),
            sp.GetRequiredService<SyncEventStream>(),
            sp.GetRequiredService<hyperwycOptions>(),
            new HttpClientHandler()));

        // hyperwycHandler is transient — each named HTTP client pipeline gets its own instance.
        services.TryAddTransient<hyperwycHandler>();

        // Startup flush hosted service.
        if (options.FlushOnStartup)
            services.AddHostedService<hyperwycHostedService>();

        return services;
    }
}
