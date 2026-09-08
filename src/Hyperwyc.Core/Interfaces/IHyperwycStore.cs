using Hyperwyc.Models;

namespace Hyperwyc.Interfaces;

/// <summary>
/// Persistence layer for request/response envelopes. Implementations provide
/// the backing store used by the handler for caching and outbox management.
/// </summary>
/// <remarks>
/// <para>
/// <b>Implementations must be safe for concurrent use, including concurrent writes.</b>
/// This is a requirement rather than a nicety: <c>HyperwycHandler</c> is registered transient
/// and runs on whatever thread its caller used, so two overlapping HTTP requests reach the
/// store at the same time, and an processor flush runs on a background task alongside
/// them. A store that serialises nothing will interleave read-modify-write sequences and,
/// if it persists to a file, can corrupt or lose writes outright.
/// </para>
/// <para>
/// Both shipped implementations serialise every operation behind a single
/// <see cref="SemaphoreSlim"/>. That is the simple answer and it is fast enough; a store with
/// finer-grained locking of its own is welcome to do better, but it must not do less.
/// </para>
/// </remarks>
public interface IHyperwycStore
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
    /// Inserts or updates <paramref name="envelope"/> in the store.
    /// </summary>
    Task UpsertAsync(Envelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Marks the envelope identified by <paramref name="id"/> as delivered and removes it
    /// from the outbox.
    /// </summary>
    Task MarkDeliveredAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Moves the envelope identified by <paramref name="id"/> to the dead-letter queue.
    /// </summary>
    /// <remarks>
    /// Not the end of a retry sequence — there is no retry budget to exhaust. An envelope is
    /// dead-lettered as soon as the server answers with a non-success status, because any
    /// answer means the request reached the API and Hyperwyc's work is done. Only a transport
    /// failure leaves an envelope in the outbox. See
    /// <see cref="Models.DeliveryOutcomeKind"/>.
    /// </remarks>
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
