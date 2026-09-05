using Microsoft.Extensions.DependencyInjection;
using Hyperwyc.Cabinet;
using Hyperwyc.Interfaces;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers the batteries-included entry point: <c>AddHyperwyc()</c> must supply durable
/// Cabinet-backed storage without being asked.
/// </summary>
/// <remarks>
/// Batteries-included stops at storage. Connectivity is still the consumer's to state
/// (issue #47), so every registration below supplies one, and one test covers the
/// failure when none is given.
/// </remarks>
public sealed class CabinetRegistrationTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"hyperwyc-reg-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void AddHyperwyc_NoConfiguration_ResolvesCabinetStore()
    {
        var services = new ServiceCollection();
        services.AddHyperwyc(Connectivity, o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IHyperwycStore>();

        Assert.IsType<CabinetStore>(store);
    }

    [Fact]
    public void AddHyperwyc_NoConfiguration_RegistersCoreServices()
    {
        var services = new ServiceCollection();
        services.AddHyperwyc(Connectivity, o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();

        // OutboxProcessor is internal to the core assembly — consumers reach
        // flushing through IHyperwyc, so that is what this asserts.
        Assert.NotNull(sp.GetService<IHyperwyc>());
        Assert.NotNull(sp.GetService<HyperwycHandler>());
    }

    [Fact]
    public void AddHyperwyc_ConfigureDelegate_IsApplied()
    {
        var services = new ServiceCollection();
        services.AddHyperwyc(
            o =>
            {
                Connectivity(o);
                o.Routes.Default = Models.RoutePolicy.CacheFirst(TimeSpan.FromHours(3));
            },
            o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<HyperwycOptions>();

        Assert.Equal(TimeSpan.FromHours(3), options.Routes.Default.Ttl);
    }

    [Fact]
    public void AddHyperwyc_StoreIsNotConstructedUntilResolved()
    {
        var services = new ServiceCollection();
        services.AddHyperwyc(Connectivity, o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();

        // Registration alone must not touch the filesystem.
        Assert.False(Directory.Exists(_tempDir));

        sp.GetRequiredService<IHyperwycStore>();

        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public void AddHyperwyc_ExplicitStoreRegistration_IsNotOverridden()
    {
        var custom = new InMemoryStore();
        var services = new ServiceCollection();
        services.AddSingleton<IHyperwycStore>(custom);
        services.AddHyperwyc(Connectivity, o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();

        Assert.Same(custom, sp.GetRequiredService<IHyperwycStore>());
    }

    [Fact]
    public void AddHyperwyc_NoConnectivity_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddHyperwyc(configureStore: o => o.DirectoryPath = _tempDir);
        using var sp = services.BuildServiceProvider();

        // Cabinet makes storage a non-decision; connectivity stays a decision, because
        // only the application knows how its platform reports it.
        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IHyperwyc>());
    }

    [Fact]
    public void AddHyperwyc_ConnectivityRegisteredAfterwards_IsUsed()
    {
        // The shortest working setup, and the one the docs lead with.
        var services = new ServiceCollection();
        services.AddHyperwyc(configureStore: o => o.DirectoryPath = _tempDir);
        services.AddSingleton<IConnectivityService, AlwaysOnlineConnectivityService>();

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<IHyperwyc>());
    }

    [Fact]
    public void AddHyperwyc_NullServices_Throws()
    {
        IServiceCollection? services = null;

        Assert.Throws<ArgumentNullException>(() => services!.AddHyperwyc());
    }

    [Fact]
    public void DefaultDirectoryPath_IsUnderLocalApplicationData()
    {
        var expectedRoot =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var path = CabinetStoreOptions.DefaultDirectoryPath();

        Assert.StartsWith(expectedRoot, path, StringComparison.Ordinal);
        Assert.EndsWith("Hyperwyc", path, StringComparison.Ordinal);
    }

    [Fact]
    public void CabinetStoreOptions_DefaultsToDerivedKey()
    {
        var options = new CabinetStoreOptions();

        Assert.Null(options.EncryptionKey);
    }

    [Fact]
    public void CabinetStore_OptionsConstructor_HonoursExplicitKey()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);

        var store = new CabinetStore(new CabinetStoreOptions
        {
            DirectoryPath = _tempDir,
            EncryptionKey = key,
        });

        Assert.NotNull(store);
    }

    [Fact]
    public void CabinetStore_OptionsConstructor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new CabinetStore((CabinetStoreOptions)null!));

    /// <summary>Supplies the required connectivity service. See issue #47.</summary>
    private static void Connectivity(HyperwycOptions options) =>
        options.Connectivity = new AlwaysOnlineConnectivityService();
}
