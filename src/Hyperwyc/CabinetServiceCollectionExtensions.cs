using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Hyperwyc.Cabinet;

namespace Hyperwyc;

/// <summary>
/// Extension methods for registering Hyperwyc with durable Cabinet-backed storage.
/// </summary>
public static class CabinetServiceCollectionExtensions
{
    /// <summary>
    /// Registers Hyperwyc with <see cref="CabinetSyncStore"/> as the backing store.
    /// This is the entry point for most applications: called with no arguments it
    /// produces a working, durable configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store is written to a <c>Hyperwyc</c> folder under
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> and encrypted at
    /// rest with a key derived from that path. See
    /// <see cref="CabinetStoreOptions.EncryptionKey"/> for what that default does and
    /// does not protect against, and how to supply your own key.
    /// </para>
    /// <code>
    /// services.AddHttpClient("MyApi")
    ///     .AddHttpMessageHandler&lt;HyperwycHandler&gt;();
    ///
    /// services.AddHyperwyc();
    /// </code>
    /// <para>
    /// To use a different store, install <c>Hyperwyc.Core</c> and call
    /// <c>AddHyperwycCore&lt;TStore&gt;()</c> instead.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Optional delegate to configure <see cref="HyperwycOptions"/>.</param>
    /// <param name="configureStore">
    /// Optional delegate to configure the Cabinet store's location and encryption key.
    /// </param>
    /// <returns>The original <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddHyperwyc(
        this IServiceCollection services,
        Action<HyperwycOptions>? configure = null,
        Action<CabinetStoreOptions>? configureStore = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var storeOptions = new CabinetStoreOptions();
        configureStore?.Invoke(storeOptions);

        services.TryAddSingleton(storeOptions);

        // Registered by type so the container owns construction and disposal, and so
        // no directory is touched unless the store is actually resolved.
        return services.AddHyperwycCore<CabinetSyncStore>(configure);
    }
}
