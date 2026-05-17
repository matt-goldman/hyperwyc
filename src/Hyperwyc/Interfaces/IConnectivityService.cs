namespace hyperwyc.Interfaces;

/// <summary>
/// Abstracts platform-specific network reachability so that the core library
/// has no direct dependency on .NET MAUI or any other connectivity API.
/// </summary>
public interface IConnectivityService
{
    /// <summary>
    /// Gets a value indicating whether the device currently has network access.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// An observable sequence that emits <see langword="true"/> when connectivity
    /// is gained and <see langword="false"/> when it is lost.
    /// </summary>
    IObservable<bool> ConnectivityChanged { get; }
}
