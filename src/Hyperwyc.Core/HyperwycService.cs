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
    private readonly SyncOrchestrator _orchestrator;

    internal HyperwycService(
        SyncEventStream events,
        ISyncStore store,
        SyncOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(orchestrator);

        _events = events;
        _store = store;
        _orchestrator = orchestrator;
    }

    /// <inheritdoc/>
    public IObservable<SyncEvent> SyncEvents => _events;

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) =>
        _orchestrator.FlushAsync(ct);

    /// <inheritdoc/>
    public Task ResetStoreAsync(CancellationToken ct = default) =>
        _store.ResetAsync(ct);
}
