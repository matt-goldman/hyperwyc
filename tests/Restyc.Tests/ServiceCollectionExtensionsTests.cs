using Microsoft.Extensions.DependencyInjection;
using Restyc.Interfaces;
using Xunit;

namespace Restyc.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    // -------------------------------------------------------------------------
    // Default registrations (no configure delegate)
    // -------------------------------------------------------------------------

    [Fact]
    public void AddRestyc_Defaults_RegistersIRestyc()
    {
        var sp = BuildProvider(null);

        var restyc = sp.GetService<IRestyc>();

        Assert.NotNull(restyc);
    }

    [Fact]
    public void AddRestyc_Defaults_RegistersSyncEventStreamAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<SyncEventStream>();
        var b = sp.GetRequiredService<SyncEventStream>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddRestyc_Defaults_RegistersSyncOrchestratorAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<SyncOrchestrator>();
        var b = sp.GetRequiredService<SyncOrchestrator>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddRestyc_Defaults_RegistersRestycHandlerAsTransient()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<RestycHandler>();
        var b = sp.GetRequiredService<RestycHandler>();

        Assert.NotSame(a, b);
    }

    [Fact]
    public void AddRestyc_Defaults_RegistersISyncStoreFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<RestycOptions>();
        var store = sp.GetRequiredService<ISyncStore>();

        Assert.Same(options.Store, store);
    }

    [Fact]
    public void AddRestyc_Defaults_RegistersIConnectivityServiceFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<RestycOptions>();
        var connectivity = sp.GetRequiredService<IConnectivityService>();

        Assert.Same(options.Connectivity, connectivity);
    }

    [Fact]
    public void AddRestyc_Defaults_RegistersISyncPolicyFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<RestycOptions>();
        var policy = sp.GetRequiredService<ISyncPolicy>();

        Assert.Same(options.DefaultPolicy, policy);
    }

    [Fact]
    public void AddRestyc_Defaults_RegistersIStalenessEvaluatorFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<RestycOptions>();
        var evaluator = sp.GetRequiredService<IStalenessEvaluator>();

        Assert.Same(options.StalenessEvaluator, evaluator);
    }

    // -------------------------------------------------------------------------
    // IRestyc is a singleton backed by the same SyncEventStream
    // -------------------------------------------------------------------------

    [Fact]
    public void AddRestyc_IRestyc_IsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<IRestyc>();
        var b = sp.GetRequiredService<IRestyc>();

        Assert.Same(a, b);
    }

    // -------------------------------------------------------------------------
    // Custom store wired through options
    // -------------------------------------------------------------------------

    [Fact]
    public void AddRestyc_CustomStore_IsResolvedFromContainer()
    {
        var customStore = new InMemorySyncStore();
        var sp = BuildProvider(o => o.Store = customStore);

        var resolved = sp.GetRequiredService<ISyncStore>();

        Assert.Same(customStore, resolved);
    }

    // -------------------------------------------------------------------------
    // TTL propagation
    // -------------------------------------------------------------------------

    [Fact]
    public void AddRestyc_CacheFirstPolicy_PropagatesTtlToDefaultCacheTtl()
    {
        var ttl = TimeSpan.FromHours(3);
        var sp = BuildProvider(o => o.DefaultPolicy = SyncPolicy.CacheFirst(ttl));

        var options = sp.GetRequiredService<RestycOptions>();

        Assert.Equal(ttl, options.DefaultCacheTtl);
    }

    [Fact]
    public void AddRestyc_ApiFirstPolicy_DoesNotOverrideDefaultCacheTtl()
    {
        var sp = BuildProvider(o => o.DefaultPolicy = SyncPolicy.ApiFirst());

        var options = sp.GetRequiredService<RestycOptions>();

        // Default TTL from RestycOptions initializer should be preserved.
        Assert.True(options.DefaultCacheTtl > TimeSpan.Zero);
    }

    // -------------------------------------------------------------------------
    // Null-safety
    // -------------------------------------------------------------------------

    [Fact]
    public void AddRestyc_NullServices_Throws()
    {
        IServiceCollection? services = null;

        Assert.Throws<ArgumentNullException>(() => services!.AddRestyc());
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(Action<RestycOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddRestyc(configure);
        return services.BuildServiceProvider();
    }
}
