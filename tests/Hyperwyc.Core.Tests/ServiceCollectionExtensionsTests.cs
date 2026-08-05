using Microsoft.Extensions.DependencyInjection;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    // -------------------------------------------------------------------------
    // Default registrations (no configure delegate)
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwycCore_Defaults_RegistersIHyperwyc()
    {
        var sp = BuildProvider(null);

        var hyperwyc = sp.GetService<IHyperwyc>();

        Assert.NotNull(hyperwyc);
    }

    [Fact]
    public void AddHyperwycCore_Defaults_RegistersSyncEventStreamAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<SyncEventStream>();
        var b = sp.GetRequiredService<SyncEventStream>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddHyperwycCore_Defaults_RegistersSyncOrchestratorAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<SyncOrchestrator>();
        var b = sp.GetRequiredService<SyncOrchestrator>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddHyperwycCore_Defaults_RegistersHyperwycHandlerAsTransient()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<HyperwycHandler>();
        var b = sp.GetRequiredService<HyperwycHandler>();

        Assert.NotSame(a, b);
    }

    [Fact]
    public void AddHyperwycCore_Defaults_RegistersTStoreAsISyncStore()
    {
        var sp = BuildProvider(null);

        var store = sp.GetRequiredService<ISyncStore>();

        Assert.IsType<InMemorySyncStore>(store);
    }

    [Fact]
    public void AddHyperwycCore_StoreIsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<ISyncStore>();
        var b = sp.GetRequiredService<ISyncStore>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddHyperwycCore_Defaults_RegistersIConnectivityServiceFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<HyperwycOptions>();
        var connectivity = sp.GetRequiredService<IConnectivityService>();

        Assert.Same(options.Connectivity, connectivity);
    }

    [Fact]
    public void AddHyperwycCore_Defaults_RegistersISyncPolicyFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<HyperwycOptions>();
        var policy = sp.GetRequiredService<ISyncPolicy>();

        Assert.Same(options.DefaultPolicy, policy);
    }

    [Fact]
    public void AddHyperwycCore_Defaults_RegistersIStalenessEvaluator()
    {
        var sp = BuildProvider(null);

        var evaluator = sp.GetRequiredService<IStalenessEvaluator>();

        Assert.IsType<TtlStalenessEvaluator>(evaluator);
    }

    [Fact]
    public void AddHyperwycCore_CustomStalenessEvaluator_IsNotReplaced()
    {
        var custom = new FakeStalenessEvaluator(isStale: false);
        var sp = BuildProvider(o => o.StalenessEvaluator = custom);

        var resolved = sp.GetRequiredService<IStalenessEvaluator>();

        Assert.Same(custom, resolved);
    }

    // -------------------------------------------------------------------------
    // IHyperwyc is a singleton backed by the same SyncEventStream
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwycCore_IHyperwyc_IsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<IHyperwyc>();
        var b = sp.GetRequiredService<IHyperwyc>();

        Assert.Same(a, b);
    }

    // -------------------------------------------------------------------------
    // Store selection
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwycCore_ContainerConstructsStoreWithItsDependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new StoreDependency());
        services.AddHyperwycCore<StoreWithDependency>();

        var store = services.BuildServiceProvider().GetRequiredService<ISyncStore>();

        Assert.IsType<StoreWithDependency>(store);
    }

    [Fact]
    public void AddHyperwycCore_Factory_UsesSuppliedInstance()
    {
        var customStore = new InMemorySyncStore();
        var services = new ServiceCollection();
        services.AddHyperwycCore(_ => customStore);

        var resolved = services.BuildServiceProvider().GetRequiredService<ISyncStore>();

        Assert.Same(customStore, resolved);
    }

    [Fact]
    public void AddHyperwycCore_Factory_IsNotInvokedUntilStoreIsResolved()
    {
        var invoked = false;
        var services = new ServiceCollection();
        services.AddHyperwycCore(_ =>
        {
            invoked = true;
            return new InMemorySyncStore();
        });

        using var sp = services.BuildServiceProvider();
        Assert.False(invoked);

        sp.GetRequiredService<ISyncStore>();
        Assert.True(invoked);
    }

    // -------------------------------------------------------------------------
    // Effective TTL
    //
    // These assert the TTL the resolved evaluator actually applies, not just the
    // value left on the options object. Issue #29 was precisely a case where the
    // property was correct and the evaluator was not.
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwycCore_CacheFirstPolicyTtl_IsAppliedByTheEvaluator()
    {
        var sp = BuildProvider(o => o.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1)));

        var evaluator = sp.GetRequiredService<IStalenessEvaluator>();
        var options = sp.GetRequiredService<HyperwycOptions>();

        Assert.Equal(TimeSpan.FromDays(1), options.DefaultCacheTtl);
        Assert.False(IsStaleAfter(evaluator, TimeSpan.FromHours(23)));
        Assert.True(IsStaleAfter(evaluator, TimeSpan.FromHours(25)));
    }

    [Fact]
    public void AddHyperwycCore_ExplicitDefaultCacheTtl_IsAppliedByTheEvaluator()
    {
        var sp = BuildProvider(o => o.DefaultCacheTtl = TimeSpan.FromMinutes(1));

        var evaluator = sp.GetRequiredService<IStalenessEvaluator>();
        var options = sp.GetRequiredService<HyperwycOptions>();

        // The default policy carries no TTL, so it must not clobber this.
        Assert.Equal(TimeSpan.FromMinutes(1), options.DefaultCacheTtl);
        Assert.False(IsStaleAfter(evaluator, TimeSpan.FromSeconds(30)));
        Assert.True(IsStaleAfter(evaluator, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void AddHyperwycCore_PolicyTtl_WinsOverDefaultCacheTtl()
    {
        var sp = BuildProvider(o =>
        {
            o.DefaultCacheTtl = TimeSpan.FromMinutes(1);
            o.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromHours(6));
        });

        var evaluator = sp.GetRequiredService<IStalenessEvaluator>();

        Assert.False(IsStaleAfter(evaluator, TimeSpan.FromHours(5)));
        Assert.True(IsStaleAfter(evaluator, TimeSpan.FromHours(7)));
    }

    [Fact]
    public void AddHyperwycCore_Defaults_ApplyFiveMinuteTtl()
    {
        var sp = BuildProvider(null);

        var evaluator = sp.GetRequiredService<IStalenessEvaluator>();

        Assert.False(IsStaleAfter(evaluator, TimeSpan.FromMinutes(4)));
        Assert.True(IsStaleAfter(evaluator, TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public void AddHyperwycCore_TtllessPolicy_FallsBackToDefaultCacheTtl()
    {
        var sp = BuildProvider(o =>
        {
            o.DefaultPolicy = SyncPolicy.ApiFirst();
            o.DefaultCacheTtl = TimeSpan.FromMinutes(30);
        });

        var evaluator = sp.GetRequiredService<IStalenessEvaluator>();
        var options = sp.GetRequiredService<HyperwycOptions>();

        Assert.Equal(TimeSpan.FromMinutes(30), options.DefaultCacheTtl);
        Assert.True(IsStaleAfter(evaluator, TimeSpan.FromMinutes(31)));
    }

    // -------------------------------------------------------------------------
    // Null-safety
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwycCore_NullServices_Throws()
    {
        IServiceCollection? services = null;

        Assert.Throws<ArgumentNullException>(
            () => services!.AddHyperwycCore<InMemorySyncStore>());
    }

    [Fact]
    public void AddHyperwycCore_NullStoreFactory_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(
            () => services.AddHyperwycCore(storeFactory: null!));
    }

    // -------------------------------------------------------------------------
    // Helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asks <paramref name="evaluator"/> whether a response cached
    /// <paramref name="age"/> ago is stale.
    /// </summary>
    private static bool IsStaleAfter(IStalenessEvaluator evaluator, TimeSpan age)
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = new Envelope
        {
            Response = new CachedResponse { CachedAt = now - age },
        };

        return evaluator.IsStale(envelope, now);
    }

    private static ServiceProvider BuildProvider(Action<HyperwycOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemorySyncStore>(configure);
        return services.BuildServiceProvider();
    }

    /// <summary>A dependency the container must inject to build the store below.</summary>
    private sealed class StoreDependency;

    /// <summary>
    /// A store that is only constructible by the container, proving that
    /// <c>AddHyperwycCore&lt;TStore&gt;</c> resolves dependencies rather than
    /// requiring a parameterless constructor. Delegates to an in-memory store.
    /// </summary>
    private sealed class StoreWithDependency(StoreDependency dependency) : ISyncStore
    {
        private readonly InMemorySyncStore _inner = new();

        public StoreDependency Dependency { get; } = dependency;

        public Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default) =>
            _inner.GetCachedResponseAsync(url, ct);

        public Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default) =>
            _inner.GetPendingOutboxAsync(ct);

        public Task<IReadOnlyList<Envelope>> GetDueForRetryAsync(DateTimeOffset now, CancellationToken ct = default) =>
            _inner.GetDueForRetryAsync(now, ct);

        public Task UpsertAsync(Envelope envelope, CancellationToken ct = default) =>
            _inner.UpsertAsync(envelope, ct);

        public Task MarkSyncedAsync(string id, CancellationToken ct = default) =>
            _inner.MarkSyncedAsync(id, ct);

        public Task MoveToDeadLetterAsync(string id, CancellationToken ct = default) =>
            _inner.MoveToDeadLetterAsync(id, ct);

        public Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default) =>
            _inner.InvalidateCacheForPrefixAsync(urlPrefix, ct);

        public Task ResetAsync(CancellationToken ct = default) => _inner.ResetAsync(ct);
    }
}
