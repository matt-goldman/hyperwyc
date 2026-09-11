using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Default implementation of <see cref="IHyperwyc"/>. Exposes the sync event stream and
/// delegates flushing and store reset to the <see cref="OutboxProcessor"/>.
/// </summary>
internal sealed class HyperwycService : IHyperwyc, IHyperwycDiagnostics
{
    private readonly HyperwycEventStream _events;
    private readonly OutboxProcessor _processor;

    internal HyperwycService(HyperwycEventStream events, OutboxProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(processor);

        _events = events;
        _processor = processor;
    }

    /// <inheritdoc/>
    public IObservable<HyperwycEvent> Events => _events;

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) =>
        _processor.FlushAsync(ct);

    /// <inheritdoc/>
    public Task ResetStoreAsync(CancellationToken ct = default) =>
        _processor.ResetStoreAsync(ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<PendingItem>> GetPendingOutboxAsync(CancellationToken ct = default) =>
        _processor.GetDiagnosticViewAsync(ct);
}
