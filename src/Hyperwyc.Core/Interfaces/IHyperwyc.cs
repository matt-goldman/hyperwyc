using Hyperwyc.Models;

namespace Hyperwyc.Interfaces;

/// <summary>
/// Top-level interface for the Hyperwyc service. Exposes the real-time sync event
/// stream and allows callers to fully reset the underlying store.
/// </summary>
public interface IHyperwyc
{
    /// <summary>
    /// A hot observable that emits a <see cref="SyncEvent"/> each time a request
    /// moves through the sync lifecycle (queued, retried, synced, failed, or
    /// updated).
    /// </summary>
    IObservable<SyncEvent> SyncEvents { get; }

    /// <summary>
    /// Drains the outbox now, sending queued writes in the order they were made.
    /// Use this for a "sync now" affordance; Hyperwyc otherwise flushes on its own
    /// at startup and when connectivity is restored.
    /// </summary>
    /// <remarks>
    /// Returns immediately if a flush is already in progress. Queued writes that
    /// cannot be delivered stay in the outbox and are retried on the next flush,
    /// so calling this is safe regardless of connectivity.
    /// </remarks>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears all persisted state — outbox, cache, and dead-letter queue —
    /// and resets the store to its initial empty state.
    /// </summary>
    Task ResetStoreAsync(CancellationToken ct = default);
}
