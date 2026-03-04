using Restyc.Models;

namespace Restyc.Interfaces;

/// <summary>
/// Persistence layer for request/response envelopes. Implementations provide
/// the backing store used by the handler for caching and outbox management.
/// </summary>
public interface ISyncStore
{
    /// <summary>
    /// Returns the most-recently cached response envelope for <paramref name="url"/>,
    /// or <see langword="null"/> if no cached entry exists.
    /// </summary>
    Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Returns all envelopes currently in the outbox (write requests awaiting sync).
    /// </summary>
    Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all outbox envelopes whose next retry time is on or before
    /// <paramref name="now"/>.
    /// </summary>
    Task<IReadOnlyList<Envelope>> GetDueForRetryAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates <paramref name="envelope"/> in the store.
    /// </summary>
    Task UpsertAsync(Envelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Marks the envelope identified by <paramref name="id"/> as successfully
    /// synced and removes it from the outbox.
    /// </summary>
    Task MarkSyncedAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Moves the envelope identified by <paramref name="id"/> to the dead-letter
    /// queue after all retry attempts have been exhausted.
    /// </summary>
    Task MoveToDeadLetterAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Removes all cached GET responses whose URL begins with
    /// <paramref name="urlPrefix"/>.
    /// </summary>
    Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default);

    /// <summary>
    /// Clears all data from the store, including the outbox, cache, and
    /// dead-letter queue.
    /// </summary>
    Task ResetAsync(CancellationToken ct = default);
}
