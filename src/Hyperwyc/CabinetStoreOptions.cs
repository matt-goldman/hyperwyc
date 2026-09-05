namespace Hyperwyc.Cabinet;

/// <summary>
/// Configuration for the Cabinet-backed <see cref="CabinetStore"/>.
/// </summary>
/// <remarks>
/// Storage-specific settings live here rather than on <see cref="HyperwycOptions"/>,
/// which stays free of concepts that only apply to one store — a browser-backed store,
/// for example, has no directory path.
/// </remarks>
public sealed class CabinetStoreOptions
{
    /// <summary>
    /// Directory in which Cabinet writes its encrypted files. Created automatically if
    /// it does not exist.
    /// </summary>
    /// <remarks>
    /// Defaults to a <c>Hyperwyc</c> folder under
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/>, which resolves
    /// inside the application sandbox on Android and iOS and to the platform's
    /// per-user application data location on desktop.
    /// </remarks>
    public string DirectoryPath { get; set; } = DefaultDirectoryPath();

    /// <summary>
    /// A 32-byte (256-bit) AES-256 key protecting the store at rest, or
    /// <see langword="null"/> to derive one from <see cref="DirectoryPath"/>.
    /// </summary>
    /// <remarks>
    /// The derived default requires no configuration and protects the store against
    /// casual inspection of the device filesystem, but it is deterministic for a given
    /// path and so is not a defence against an attacker who has both the device and
    /// knowledge of this library. Supply your own key — ideally held in platform secure
    /// storage — if the cached data warrants it. Losing the key means losing access to
    /// everything already stored.
    /// </remarks>
    public byte[]? EncryptionKey { get; set; }

    /// <summary>
    /// Returns the default store directory:
    /// <c>{LocalApplicationData}/Hyperwyc</c>.
    /// </summary>
    public static string DefaultDirectoryPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hyperwyc");
}
