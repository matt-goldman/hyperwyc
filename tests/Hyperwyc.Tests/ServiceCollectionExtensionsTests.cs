using Microsoft.Extensions.DependencyInjection;
using Hyperwyc.Interfaces;
using Xunit;

namespace Hyperwyc.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    // -------------------------------------------------------------------------
    // Default registrations (no configure delegate)
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwyc_Defaults_RegistersIHyperwyc()
    {
        var sp = BuildProvider(null);

        var hyperwyc = sp.GetService<IHyperwyc>();

        Assert.NotNull(Hyperwyc);
    }

    [Fact]
    public void AddHyperwyc_Defaults_RegistersSyncEventStreamAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<SyncEventStream>();
        var b = sp.GetRequiredService<SyncEventStream>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddHyperwyc_Defaults_RegistersSyncOrchestratorAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<SyncOrchestrator>();
        var b = sp.GetRequiredService<SyncOrchestrator>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddHyperwyc_Defaults_RegistersHyperwycHandlerAsTransient()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<HyperwycHandler>();
        var b = sp.GetRequiredService<HyperwycHandler>();

        Assert.NotSame(a, b);
    }

    [Fact]
    public void AddHyperwyc_Defaults_RegistersISyncStoreFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<HyperwycOptions>();
        var store = sp.GetRequiredService<ISyncStore>();

        Assert.Same(options.Store, store);
    }

    [Fact]
    public void AddHyperwyc_Defaults_RegistersIConnectivityServiceFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<HyperwycOptions>();
        var connectivity = sp.GetRequiredService<IConnectivityService>();

        Assert.Same(options.Connectivity, connectivity);
    }

    [Fact]
    public void AddHyperwyc_Defaults_RegistersISyncPolicyFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<HyperwycOptions>();
        var policy = sp.GetRequiredService<ISyncPolicy>();

        Assert.Same(options.DefaultPolicy, policy);
    }

    [Fact]
    public void AddHyperwyc_Defaults_RegistersIStalenessEvaluatorFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<HyperwycOptions>();
        var evaluator = sp.GetRequiredService<IStalenessEvaluator>();

        Assert.Same(options.StalenessEvaluator, evaluator);
    }

    // -------------------------------------------------------------------------
    // IHyperwyc is a singleton backed by the same SyncEventStream
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwyc_IHyperwyc_IsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<IHyperwyc>();
        var b = sp.GetRequiredService<IHyperwyc>();

        Assert.Same(a, b);
    }

    // -------------------------------------------------------------------------
    // Custom store wired through options
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwyc_CustomStore_IsResolvedFromContainer()
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
    public void AddHyperwyc_CacheFirstPolicy_PropagatesTtlToDefaultCacheTtl()
    {
        var ttl = TimeSpan.FromHours(3);
        var sp = BuildProvider(o => o.DefaultPolicy = SyncPolicy.CacheFirst(ttl));

        var options = sp.GetRequiredService<HyperwycOptions>();

        Assert.Equal(ttl, options.DefaultCacheTtl);
    }

    [Fact]
    public void AddHyperwyc_ApiFirstPolicy_DoesNotOverrideDefaultCacheTtl()
    {
        var sp = BuildProvider(o => o.DefaultPolicy = SyncPolicy.ApiFirst());

        var options = sp.GetRequiredService<HyperwycOptions>();

        // Default TTL from HyperwycOptions initializer should be preserved.
        Assert.True(options.DefaultCacheTtl > TimeSpan.Zero);
    }

    // -------------------------------------------------------------------------
    // Null-safety
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwyc_NullServices_Throws()
    {
        IServiceCollection? services = null;

        Assert.Throws<ArgumentNullException>(() => services!.AddHyperwyc());
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(Action<HyperwycOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddHyperwyc(configure);
        return services.BuildServiceProvider();
    }
}
