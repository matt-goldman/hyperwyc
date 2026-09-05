using Hyperwyc.Interfaces;

namespace Hyperwyc;

/// <summary>
/// An <see cref="IConnectivityService"/> that always reports the device as connected. For a
/// host that genuinely is, and for tests that are online throughout.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the fallback</b>, and not a way to opt out of thinking about connectivity — that is
/// <see cref="NetworkAvailabilityConnectivityService"/>, which Hyperwyc uses when nothing is
/// registered.
/// </para>
/// <para>
/// <b><see cref="ConnectivityChanged"/> never emits.</b> Writes are still queued when the
/// transport cannot deliver them, so nothing is lost, but no signal will ever cause the outbox
/// to drain: the only triggers left are <c>FlushOnStartup</c> and an explicit
/// <c>IHyperwyc.FlushAsync()</c>. Under this service, replaying is the application's
/// responsibility — a scheduled job, a user-facing control, or a duty cycle on a headless
/// device. Reads pay a full transport failure before falling back to the store.
/// </para>
/// </remarks>
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
