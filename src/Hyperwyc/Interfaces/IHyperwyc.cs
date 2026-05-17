using hyperwyc.Models;

namespace hyperwyc.Interfaces;

/// <summary>
/// Top-level interface for the hyperwyc service. Exposes the real-time sync event
/// stream and allows callers to fully reset the underlying store.
/// </summary>
public interface Ihyperwyc
{
    /// <summary>
    /// A hot observable that emits a <see cref="SyncEvent"/> each time a request
    /// moves through the sync lifecycle (queued, retried, synced, failed, or
    /// updated).
    /// </summary>
    IObservable<SyncEvent> SyncEvents { get; }

    /// <summary>
    /// Clears all persisted state — outbox, cache, and dead-letter queue —
    /// and resets the store to its initial empty state.
    /// </summary>
    Task ResetStoreAsync(CancellationToken ct = default);
}
