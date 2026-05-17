using Hyperwyc.Interfaces;

namespace Hyperwyc.Tests.Fakes;

/// <summary>
/// Test double for <see cref="IConnectivityService"/>.
/// Set <see cref="IsConnected"/> to control the connectivity state.
/// <see cref="ConnectivityChanged"/> never emits an event.
/// </summary>
internal sealed class FakeConnectivityService : IConnectivityService
{
    public bool IsConnected { get; set; }

    public IObservable<bool> ConnectivityChanged { get; } = new NeverObservable();

    public FakeConnectivityService(bool isConnected = true) => IsConnected = isConnected;

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
