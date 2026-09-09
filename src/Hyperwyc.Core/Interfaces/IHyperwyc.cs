using Hyperwyc.Models;

namespace Hyperwyc.Interfaces;

/// <summary>
/// Top-level interface for the Hyperwyc service. Exposes the real-time sync event
/// stream and allows callers to fully reset the underlying store.
/// </summary>
public interface IHyperwyc
{
    /// <summary>
    /// A hot observable that emits a <see cref="HyperwycEvent"/> each time a request
    /// moves through its lifecycle (queued, delivered, or updated).
    /// </summary>
    /// <remarks>
    /// Hot, with no replay, and for a delivery it is the <b>only</b> report there is — nothing
    /// about a delivered write is retained (ADR 0010). Subscribe before anything can flush;
    /// <see cref="HyperwycOptions.FlushOnStartup"/> is off by default so that the composition
    /// order is yours rather than the host's.
    /// </remarks>
    IObservable<HyperwycEvent> Events { get; }

    /// <summary>
    /// Drains the outbox now, sending queued writes in the order they were made.
    /// Use this for a "sync now" affordance, and at startup once your event subscriber is in
    /// place; Hyperwyc otherwise flushes on its own only when connectivity is restored.
    /// </summary>
    /// <remarks>
    /// Returns immediately if a flush is already in progress. A write whose transport
    /// could not reach the API stays in the outbox and goes out on the next flush; one
    /// the server answered is delivered and discarded, whatever it answered. There is no
    /// schedule and no backoff — the triggers are startup (off by default), connectivity
    /// restored, and this method — so calling it is safe regardless of connectivity.
    /// </remarks>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears all persisted state — the outbox and the cache — and resets the store to its
    /// initial empty state.
    /// </summary>
    Task ResetStoreAsync(CancellationToken ct = default);
}
