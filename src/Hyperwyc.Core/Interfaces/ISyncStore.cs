using Hyperwyc.Models;

namespace Hyperwyc.Interfaces;

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
    /// Returns the outbox envelopes eligible to be sent at <paramref name="now"/>:
    /// those never attempted, plus those whose scheduled retry time has arrived.
    /// </summary>
    /// <remarks>
    /// An envelope that failed transiently carries a <see cref="Envelope.NextRetryUtc"/>
    /// in the future and is deliberately excluded until then, so a flush does not
    /// re-attempt work that is waiting. This is the query a flush drains;
    /// <see cref="GetPendingOutboxAsync"/> reports everything queued regardless of
    /// readiness, which is what a diagnostics view wants.
    /// </remarks>
    Task<IReadOnlyList<Envelope>> GetReadyToSendAsync(DateTimeOffset now, CancellationToken ct = default);

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
