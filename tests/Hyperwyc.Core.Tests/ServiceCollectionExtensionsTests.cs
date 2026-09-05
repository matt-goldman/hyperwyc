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
    public void AddHyperwycCore_Defaults_RegistersHyperwycEventStreamAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<HyperwycEventStream>();
        var b = sp.GetRequiredService<HyperwycEventStream>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddHyperwycCore_Defaults_RegistersOutboxProcessorAsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<OutboxProcessor>();
        var b = sp.GetRequiredService<OutboxProcessor>();

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
    public void AddHyperwycCore_Defaults_RegistersTStoreAsIHyperwycStore()
    {
        var sp = BuildProvider(null);

        var store = sp.GetRequiredService<IHyperwycStore>();

        Assert.IsType<InMemoryStore>(store);
    }

    [Fact]
    public void AddHyperwycCore_StoreIsSingleton()
    {
        var sp = BuildProvider(null);

        var a = sp.GetRequiredService<IHyperwycStore>();
        var b = sp.GetRequiredService<IHyperwycStore>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddHyperwycCore_RegistersIConnectivityServiceFromOptionsInstance()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<HyperwycOptions>();
        var connectivity = sp.GetRequiredService<IConnectivityService>();

        Assert.Same(options.Connectivity, connectivity);
    }




    // -------------------------------------------------------------------------
    // IHyperwyc is a singleton backed by the same HyperwycEventStream
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
    // IHyperwyc is the whole consumer-facing surface
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IHyperwyc_FlushAsync_DrainsTheOutbox()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(new Envelope { Url = "https://example.com/api/x", Method = "POST" });

        var transport = new StubHttpMessageHandler(
            new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var services = new ServiceCollection();
        services.AddHyperwycCore(_ => store, WithConnectivity(o => o.ReplayTransport = transport));
        using var sp = services.BuildServiceProvider();

        // Reaching a flush must not require the concrete orchestrator, which is internal.
        await sp.GetRequiredService<IHyperwyc>().FlushAsync();

        Assert.Equal(1, transport.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task ReplayTransport_IsNotDisposedByHyperwyc()
    {
        var transport = new DisposalTrackingHandler();

        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>(WithConnectivity(o => o.ReplayTransport = transport));
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IHyperwyc>();

        await sp.DisposeAsync();

        // The caller supplied it, so the caller still owns it.
        Assert.False(transport.WasDisposed);
    }

    [Fact]
    public void IHyperwyc_ExposesEvents()
    {
        var sp = BuildProvider(null);

        var hyperwyc = sp.GetRequiredService<IHyperwyc>();

        Assert.NotNull(hyperwyc.Events);
    }

    // -------------------------------------------------------------------------
    // Store selection
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwycCore_ContainerConstructsStoreWithItsDependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new StoreDependency());
        services.AddHyperwycCore<StoreWithDependency>(WithConnectivity());

        var store = services.BuildServiceProvider().GetRequiredService<IHyperwycStore>();

        Assert.IsType<StoreWithDependency>(store);
    }

    [Fact]
    public void AddHyperwycCore_Factory_UsesSuppliedInstance()
    {
        var customStore = new InMemoryStore();
        var services = new ServiceCollection();
        services.AddHyperwycCore(_ => customStore, WithConnectivity());

        var resolved = services.BuildServiceProvider().GetRequiredService<IHyperwycStore>();

        Assert.Same(customStore, resolved);
    }

    [Fact]
    public void AddHyperwycCore_Factory_IsNotInvokedUntilStoreIsResolved()
    {
        var invoked = false;
        var services = new ServiceCollection();
        services.AddHyperwycCore(
            _ =>
            {
                invoked = true;
                return new InMemoryStore();
            },
            WithConnectivity());

        using var sp = services.BuildServiceProvider();
        Assert.False(invoked);

        sp.GetRequiredService<IHyperwycStore>();
        Assert.True(invoked);
    }

    // -------------------------------------------------------------------------
    // Route policy
    //
    // The "effective TTL" tests that lived here are gone with the ambiguity they guarded:
    // a TTL used to come from either DefaultCacheTtl or the policy, resolved at registration,
    // which is what issue #29 was. A resolved RoutePolicy now carries its own concrete TTL and
    // there is nothing to reconcile. Matching itself is covered by RoutePolicyMapTests.
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwycCore_Defaults_ApplyAOneDayTtl()
    {
        var sp = BuildProvider(null);

        var options = sp.GetRequiredService<HyperwycOptions>();

        Assert.Equal(TimeSpan.FromDays(1), options.Routes.Default.Ttl);
        Assert.Equal(SourcePriority.CacheFirst, options.Routes.Default.SourcePriority);
    }

    [Fact]
    public void AddHyperwycCore_RouteRegistrationsSurviveRegistration()
    {
        var sp = BuildProvider(o => o.Routes
            .For("/api/*", RoutePolicy.CacheFirst(TimeSpan.FromHours(1)))
            .For("/api/payments/*", RoutePolicy.NetworkOnly()));

        var routes = sp.GetRequiredService<HyperwycOptions>().Routes;

        Assert.Equal(SourcePriority.NetworkOnly,
            routes.PolicyFor(new Uri("https://example.com/api/payments/charge")).SourcePriority);
        Assert.Equal(TimeSpan.FromHours(1),
            routes.PolicyFor(new Uri("https://example.com/api/products")).Ttl);
    }

    // -------------------------------------------------------------------------
    // Connectivity is required
    //
    // There is no safe default: silently assuming "always online" produces a
    // library that caches but never queues or replays. See issue #47.
    //
    // A container registration is the expected route, so these also pin that it
    // works in either order relative to AddHyperwyc — a source generator wiring
    // one up has no say in where its registration lands.
    // -------------------------------------------------------------------------

    [Fact]
    public void ConnectivityRegisteredBeforeHyperwyc_IsUsed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService, AlwaysOnlineConnectivityService>();

        services.AddHyperwycCore<InMemoryStore>();

        using var sp = services.BuildServiceProvider();
        Assert.IsType<AlwaysOnlineConnectivityService>(sp.GetRequiredService<IConnectivityService>());
    }

    [Fact]
    public void ConnectivityRegisteredAfterHyperwyc_IsUsed()
    {
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>();

        services.AddSingleton<IConnectivityService, AlwaysOnlineConnectivityService>();

        using var sp = services.BuildServiceProvider();
        Assert.IsType<AlwaysOnlineConnectivityService>(sp.GetRequiredService<IConnectivityService>());
    }

    [Fact]
    public void ConnectivityRegisteredAfterHyperwyc_ReachesTheOrchestrator()
    {
        // Resolving the interface directly is not enough: the placeholder must also be out
        // of the way for everything Hyperwyc injects it into.
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>(o => o.FlushOnStartup = false);
        services.AddSingleton<IConnectivityService>(new FakeConnectivityService(isConnected: true));

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IHyperwyc>());
        Assert.NotNull(sp.GetRequiredService<HyperwycHandler>());
    }

    [Fact]
    public void ConnectivityInBothPlaces_TheContainerRegistrationWins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService, AlwaysOnlineConnectivityService>();

        services.AddHyperwycCore<InMemoryStore>(
            o => o.Connectivity = new NetworkAvailabilityConnectivityService());

        using var sp = services.BuildServiceProvider();

        // TryAdd leaves the earlier registration alone, so an explicit container
        // registration beats the option — the same precedence every other service
        // here follows. Recorded so it is not accidental.
        Assert.IsType<AlwaysOnlineConnectivityService>(sp.GetRequiredService<IConnectivityService>());
    }

    [Fact]
    public void NoConnectivityAnywhere_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>();
        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => sp.GetRequiredService<IConnectivityService>());

        // The message has to name the ways out, because the fix is a choice.
        Assert.Contains("AddSingleton<IConnectivityService", ex.Message);
        Assert.Contains(nameof(NetworkAvailabilityConnectivityService), ex.Message);
        Assert.Contains(nameof(AlwaysOnlineConnectivityService), ex.Message);
    }

    [Fact]
    public void NoConnectivityAnywhere_ThrowsWhenTheHandlerIsResolved()
    {
        // The failure a consumer who ignored the docs actually meets: the first request.
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>(o => o.FlushOnStartup = false);
        using var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IHyperwyc>());
    }

    [Fact]
    public void NoConnectivityAnywhere_RegistrationItselfDoesNotThrow()
    {
        var services = new ServiceCollection();

        // Deliberate: throwing here would make the container route order-dependent, so the
        // check has to wait until something asks for the service.
        services.AddHyperwycCore<InMemoryStore>();
    }

    // -------------------------------------------------------------------------
    // Null-safety
    // -------------------------------------------------------------------------

    [Fact]
    public void AddHyperwycCore_NullServices_Throws()
    {
        IServiceCollection? services = null;

        Assert.Throws<ArgumentNullException>(
            () => services!.AddHyperwycCore<InMemoryStore>());
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


    private static ServiceProvider BuildProvider(Action<HyperwycOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddHyperwycCore<InMemoryStore>(WithConnectivity(configure));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Connectivity has no default and registration throws without one (issue #47), so
    /// every registration in this file states one before applying the test's own
    /// configuration.
    /// </summary>
    private static Action<HyperwycOptions> WithConnectivity(
        Action<HyperwycOptions>? configure = null) =>
        o =>
        {
            o.Connectivity = new AlwaysOnlineConnectivityService();
            configure?.Invoke(o);
        };

    /// <summary>Records whether anything disposed it.</summary>
    private sealed class DisposalTrackingHandler : HttpMessageHandler
    {
        public bool WasDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>A dependency the container must inject to build the store below.</summary>
    private sealed class StoreDependency;

    /// <summary>
    /// A store that is only constructible by the container, proving that
    /// <c>AddHyperwycCore&lt;TStore&gt;</c> resolves dependencies rather than
    /// requiring a parameterless constructor. Delegates to an in-memory store.
    /// </summary>
    private sealed class StoreWithDependency(StoreDependency dependency) : IHyperwycStore
    {
        private readonly InMemoryStore _inner = new();

        public StoreDependency Dependency { get; } = dependency;

        public Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default) =>
            _inner.GetCachedResponseAsync(url, ct);

        public Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default) =>
            _inner.GetPendingOutboxAsync(ct);

        public Task UpsertAsync(Envelope envelope, CancellationToken ct = default) =>
            _inner.UpsertAsync(envelope, ct);

        public Task MarkDeliveredAsync(string id, CancellationToken ct = default) =>
            _inner.MarkDeliveredAsync(id, ct);

        public Task MoveToDeadLetterAsync(string id, CancellationToken ct = default) =>
            _inner.MoveToDeadLetterAsync(id, ct);

        public Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default) =>
            _inner.InvalidateCacheForPrefixAsync(urlPrefix, ct);

        public Task ResetAsync(CancellationToken ct = default) => _inner.ResetAsync(ct);
    }
}
