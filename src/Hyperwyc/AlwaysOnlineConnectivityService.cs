using hyperwyc.Interfaces;

namespace hyperwyc;

/// <summary>
/// An <see cref="IConnectivityService"/> that always reports the device as
/// connected. Use this as the default when no platform connectivity service
/// is available (e.g. in server-side apps or unit tests).
/// </summary>
public sealed class AlwaysOnlineConnectivityService : IConnectivityService
{
    /// <inheritdoc/>
    public bool IsConnected => true;

    /// <inheritdoc/>
    public IObservable<bool> ConnectivityChanged { get; } = new NeverObservable();

    private sealed class NeverObservable : IObservable<bool>
    {
        public IDisposable Subscribe(IObserver<bool> observer) => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
