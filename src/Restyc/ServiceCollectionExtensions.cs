using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Restyc.Interfaces;

namespace Restyc;

/// <summary>
/// Extension methods for registering Restyc with a .NET DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Restyc services with the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use the returned <see cref="IServiceCollection"/> to add Restyc handlers
    /// to named HTTP clients:
    /// </para>
    /// <code>
    /// services.AddHttpClient("MyApi")
    ///     .AddHttpMessageHandler&lt;RestycHandler&gt;();
    ///
    /// services.AddRestyc(options =>
    /// {
    ///     options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1));
    ///     options.Store = new CabinetSyncStore("restyc.db");
    ///     options.Connectivity = new MauiConnectivityService();
    /// });
    /// </code>
    /// </remarks>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Optional delegate to configure <see cref="RestycOptions"/>.</param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddRestyc(
        this IServiceCollection services,
        Action<RestycOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RestycOptions();
        configure?.Invoke(options);

        // If the default policy carries a TTL, propagate it to DefaultCacheTtl
        // so TtlStalenessEvaluator and other consumers stay in sync.
        if (options.DefaultPolicy is SyncPolicy.PresetSyncPolicy { Ttl: { } policyTtl })
            options.DefaultCacheTtl = policyTtl;

        // Register the options object itself as a singleton so RestycHandler
        // and SyncOrchestrator can receive it via constructor injection.
        services.AddSingleton(options);

        // Interfaces resolved from the options instance.
        services.TryAddSingleton<ISyncStore>(_ => options.Store);
        services.TryAddSingleton<IConnectivityService>(_ => options.Connectivity);
        services.TryAddSingleton<ISyncPolicy>(_ => options.DefaultPolicy);
        services.TryAddSingleton<IStalenessEvaluator>(_ => options.StalenessEvaluator);

        // Core singletons.
        services.TryAddSingleton<SyncEventStream>();

        services.TryAddSingleton<IRestyc>(sp => new RestycService(
            sp.GetRequiredService<SyncEventStream>(),
            sp.GetRequiredService<ISyncStore>()));

        services.TryAddSingleton<SyncOrchestrator>(sp => new SyncOrchestrator(
            sp.GetRequiredService<ISyncStore>(),
            sp.GetRequiredService<ISyncPolicy>(),
            sp.GetRequiredService<IConnectivityService>(),
            sp.GetRequiredService<SyncEventStream>(),
            sp.GetRequiredService<RestycOptions>(),
            new HttpClientHandler()));

        // RestycHandler is transient — each named HTTP client pipeline gets its own instance.
        services.TryAddTransient<RestycHandler>();

        // Startup flush hosted service.
        if (options.FlushOnStartup)
            services.AddHostedService<RestycHostedService>();

        return services;
    }
}
