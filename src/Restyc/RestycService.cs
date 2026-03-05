using Restyc.Interfaces;
using Restyc.Models;

namespace Restyc;

/// <summary>
/// Default implementation of <see cref="IRestyc"/>. Exposes the sync event
/// stream and delegates store reset to the configured <see cref="ISyncStore"/>.
/// </summary>
internal sealed class RestycService : IRestyc
{
    private readonly SyncEventStream _events;
    private readonly ISyncStore _store;

    internal RestycService(SyncEventStream events, ISyncStore store)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(store);

        _events = events;
        _store = store;
    }

    /// <inheritdoc/>
    public IObservable<SyncEvent> SyncEvents => _events;

    /// <inheritdoc/>
    public Task ResetStoreAsync(CancellationToken ct = default) =>
        _store.ResetAsync(ct);
}
