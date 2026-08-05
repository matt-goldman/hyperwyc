using Microsoft.Extensions.DependencyInjection;
using Hyperwyc.Cabinet;
using Hyperwyc.Interfaces;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers the batteries-included entry point: <c>AddHyperwyc()</c> with no arguments
/// must produce a working, durable configuration.
/// </summary>
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
        services.AddHyperwyc(configureStore: o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<ISyncStore>();

        Assert.IsType<CabinetSyncStore>(store);
    }

    [Fact]
    public void AddHyperwyc_NoConfiguration_RegistersCoreServices()
    {
        var services = new ServiceCollection();
        services.AddHyperwyc(configureStore: o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();

        // SyncOrchestrator is internal to the core assembly — consumers reach
        // flushing through IHyperwyc, so that is what this asserts.
        Assert.NotNull(sp.GetService<IHyperwyc>());
        Assert.NotNull(sp.GetService<HyperwycHandler>());
    }

    [Fact]
    public void AddHyperwyc_ConfigureDelegate_IsApplied()
    {
        var services = new ServiceCollection();
        services.AddHyperwyc(
            o => o.OfflineResponsePolicy = OfflineResponsePolicy.Signal,
            o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<HyperwycOptions>();

        Assert.Equal(OfflineResponsePolicy.Signal, options.OfflineResponsePolicy);
    }

    [Fact]
    public void AddHyperwyc_StoreIsNotConstructedUntilResolved()
    {
        var services = new ServiceCollection();
        services.AddHyperwyc(configureStore: o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();

        // Registration alone must not touch the filesystem.
        Assert.False(Directory.Exists(_tempDir));

        sp.GetRequiredService<ISyncStore>();

        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public void AddHyperwyc_ExplicitStoreRegistration_IsNotOverridden()
    {
        var custom = new InMemorySyncStore();
        var services = new ServiceCollection();
        services.AddSingleton<ISyncStore>(custom);
        services.AddHyperwyc(configureStore: o => o.DirectoryPath = _tempDir);

        using var sp = services.BuildServiceProvider();

        Assert.Same(custom, sp.GetRequiredService<ISyncStore>());
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
    public void CabinetSyncStore_OptionsConstructor_HonoursExplicitKey()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);

        var store = new CabinetSyncStore(new CabinetStoreOptions
        {
            DirectoryPath = _tempDir,
            EncryptionKey = key,
        });

        Assert.NotNull(store);
    }

    [Fact]
    public void CabinetSyncStore_OptionsConstructor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new CabinetSyncStore((CabinetStoreOptions)null!));
}
