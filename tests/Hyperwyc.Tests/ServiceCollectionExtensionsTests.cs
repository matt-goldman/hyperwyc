using Microsoft.Extensions.DependencyInjection;
using hyperwyc.Interfaces;
using Xunit;

namespace hyperwyc.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    // -------------------------------------------------------------------------
    // Default registrations (no configure delegate)
    // -------------------------------------------------------------------------

    [Fact]
    public void Addhyperwyc_Defaults_RegistersIhyperwyc()
    {
        var sp = BuildProvider(null);

        var hyperwyc = sp.GetService<Ihyperwyc>();

        Assert.NotNull(hyperwyc);
    }

    [Fact]
    public void Addhyperwyc_Defaults_RegistersSyncEventStreamAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<SyncEventStream>();
        var b = sp.GetRequiredService<SyncEventStream>();

        Assert.Same(a, b);
    }

    [Fact]
    public void Addhyperwyc_Defaults_RegistersSyncOrchestratorAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<SyncOrchestrator>();
        var b = sp.GetRequiredService<SyncOrchestrator>();

        Assert.Same(a, b);
    }

    [Fact]
    public void Addhyperwyc_Defaults_RegistershyperwycHandlerAsTransient()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<hyperwycHandler>();
        var b = sp.GetRequiredService<hyperwycHandler>();

        Assert.NotSame(a, b);
    }

    [Fact]
    public void Addhyperwyc_Defaults_RegistersISyncStoreFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<hyperwycOptions>();
        var store = sp.GetRequiredService<ISyncStore>();

        Assert.Same(options.Store, store);
    }

    [Fact]
    public void Addhyperwyc_Defaults_RegistersIConnectivityServiceFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<hyperwycOptions>();
        var connectivity = sp.GetRequiredService<IConnectivityService>();

        Assert.Same(options.Connectivity, connectivity);
    }

    [Fact]
    public void Addhyperwyc_Defaults_RegistersISyncPolicyFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<hyperwycOptions>();
        var policy = sp.GetRequiredService<ISyncPolicy>();

        Assert.Same(options.DefaultPolicy, policy);
    }

    [Fact]
    public void Addhyperwyc_Defaults_RegistersIStalenessEvaluatorFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<hyperwycOptions>();
        var evaluator = sp.GetRequiredService<IStalenessEvaluator>();

        Assert.Same(options.StalenessEvaluator, evaluator);
    }

    // -------------------------------------------------------------------------
    // Ihyperwyc is a singleton backed by the same SyncEventStream
    // -------------------------------------------------------------------------

    [Fact]
    public void Addhyperwyc_Ihyperwyc_IsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<Ihyperwyc>();
        var b = sp.GetRequiredService<Ihyperwyc>();

        Assert.Same(a, b);
    }

    // -------------------------------------------------------------------------
    // Custom store wired through options
    // -------------------------------------------------------------------------

    [Fact]
    public void Addhyperwyc_CustomStore_IsResolvedFromContainer()
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
    public void Addhyperwyc_CacheFirstPolicy_PropagatesTtlToDefaultCacheTtl()
    {
        var ttl = TimeSpan.FromHours(3);
        var sp = BuildProvider(o => o.DefaultPolicy = SyncPolicy.CacheFirst(ttl));

        var options = sp.GetRequiredService<hyperwycOptions>();

        Assert.Equal(ttl, options.DefaultCacheTtl);
    }

    [Fact]
    public void Addhyperwyc_ApiFirstPolicy_DoesNotOverrideDefaultCacheTtl()
    {
        var sp = BuildProvider(o => o.DefaultPolicy = SyncPolicy.ApiFirst());

        var options = sp.GetRequiredService<hyperwycOptions>();

        // Default TTL from hyperwycOptions initializer should be preserved.
        Assert.True(options.DefaultCacheTtl > TimeSpan.Zero);
    }

    // -------------------------------------------------------------------------
    // Null-safety
    // -------------------------------------------------------------------------

    [Fact]
    public void Addhyperwyc_NullServices_Throws()
    {
        IServiceCollection? services = null;

        Assert.Throws<ArgumentNullException>(() => services!.Addhyperwyc());
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    private static ServiceProvider BuildProvider(Action<hyperwycOptions>? configure)
    {
        var services = new ServiceCollection();
        services.Addhyperwyc(configure);
        return services.BuildServiceProvider();
    }
}
