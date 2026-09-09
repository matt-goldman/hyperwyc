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
    /// <b>Removes</b> the delivered envelope identified by <paramref name="id"/>, request and all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Removes rather than flags, and the name says so because the distinction is the contract.
    /// A write is delivered as soon as the server answers — with anything at all, a refusal as
    /// much as an acceptance — and at that point Hyperwyc is finished with it. What the server said
    /// goes out on <see cref="IHyperwyc.Events"/> and is not retained: it is an ordinary HTTP
    /// response, and keeping one is the application's business. See
    /// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/docs/decisions/0010-delivery-ends-hyperwycs-interest.md">ADR 0010</see>.
    /// </para>
    /// <para>
    /// The request goes with it, deliberately: its body and headers are the largest and most
    /// sensitive things in the store, and holding them past delivery extends that exposure for
    /// nothing. An implementation backed by files should delete the bytes, not just the record.
    /// </para>
    /// <para>
    /// Only a transport failure leaves an envelope in the store — see
    /// <see cref="Models.DeliveryOutcomeKind"/>.
    /// </para>
    /// </remarks>
    Task RemoveDeliveredAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Removes all cached GET responses whose URL begins with
    /// <paramref name="urlPrefix"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Remove the record, do not merely clear its response.</b> An envelope whose
    /// <see cref="Envelope.Response"/> is null is reachable by nothing —
    /// <see cref="GetCachedResponseAsync"/> skips it for the null and
    /// <see cref="GetPendingOutboxAsync"/> skips it because a cache envelope is synced — so it
    /// occupies the store until that exact URL is fetched again. Invalidation matches a prefix,
    /// so the entries it leaves behind are the ones least likely to be refetched.
    /// </para>
    /// <para>
    /// <b>Match only envelopes that have a response.</b> A queued write under the same prefix must
    /// not be touched, and the filter is what makes removal safe rather than destructive.
    /// </para>
    /// </remarks>
    Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default);

    /// <summary>
    /// Clears all data from the store — the outbox and the cache alike.
    /// </summary>
    Task ResetAsync(CancellationToken ct = default);
}
