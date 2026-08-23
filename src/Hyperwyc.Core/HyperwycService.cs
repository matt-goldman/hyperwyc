using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Default implementation of <see cref="IHyperwyc"/>. Exposes the sync event stream and
/// delegates flushing and store reset to the <see cref="SyncOrchestrator"/>.
/// </summary>
internal sealed class HyperwycService : IHyperwyc
{
    private readonly SyncEventStream _events;
    private readonly SyncOrchestrator _orchestrator;

    internal HyperwycService(SyncEventStream events, SyncOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(orchestrator);

        _events = events;
        _orchestrator = orchestrator;
    }

    /// <inheritdoc/>
    public IObservable<SyncEvent> SyncEvents => _events;

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) =>
        _orchestrator.FlushAsync(ct);

    /// <inheritdoc/>
    public Task ResetStoreAsync(CancellationToken ct = default) =>
        _orchestrator.ResetStoreAsync(ct);
}
