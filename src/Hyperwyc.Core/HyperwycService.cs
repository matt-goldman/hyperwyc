using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Default implementation of <see cref="IHyperwyc"/>. Exposes the sync event
/// stream and delegates store reset to the configured <see cref="ISyncStore"/>.
/// </summary>
internal sealed class HyperwycService : IHyperwyc
{
    private readonly SyncEventStream _events;
    private readonly ISyncStore _store;

    internal HyperwycService(SyncEventStream events, ISyncStore store)
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
