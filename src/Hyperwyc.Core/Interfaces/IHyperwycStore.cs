using Hyperwyc.Models;

namespace Hyperwyc.Interfaces;

/// <summary>
/// Persistence for the two things Hyperwyc holds: responses cached for offline reads, and
/// writes queued for delivery.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds of record, two types, two sets of methods.</b> A <see cref="CachedResponse"/> is
/// keyed by its <see cref="CachedResponse.Url"/> and a <see cref="QueuedWrite"/> by its
/// generated <see cref="QueuedWrite.Id"/>, and nothing here mixes them. They were one type told
/// apart by a boolean, which meant every query filtered on a field to infer a kind — see
/// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/Backlog/Done/55-envelope-kind-discriminator.md">issue 55</see>.
/// An implementation is free to keep both in one collection; it is not free to let one method's
/// records reach the other's.
/// </para>
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
    // -------------------------------------------------------------------------
    // Cache
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the cached response for <paramref name="url"/>, or <see langword="null"/> if
    /// nothing is cached for it.
    /// </summary>
    /// <remarks>
    /// Match the URL exactly. Whether the entry is too old to serve is not the store's
    /// judgement — the handler applies the route's TTL to what it gets back.
    /// </remarks>
    Task<CachedResponse?> GetCachedResponseAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Stores <paramref name="response"/>, replacing whatever was cached for its
    /// <see cref="CachedResponse.Url"/>.
    /// </summary>
    /// <remarks>
    /// Keyed on the URL, and replacing rather than appending is the contract: a refetched URL
    /// must overwrite, or the cache accumulates one entry per fetch and serves the oldest of
    /// them forever. See <see cref="CachedResponse.Url"/>.
    /// </remarks>
    Task PutCachedResponseAsync(CachedResponse response, CancellationToken ct = default);

    /// <summary>
    /// Removes all cached responses whose URL begins with <paramref name="urlPrefix"/>.
    /// </summary>
    /// <remarks>
    /// An ordinal <c>StartsWith</c>. <b>Remove the record, do not merely clear its body.</b> An
    /// entry with nothing in it is reachable by nothing and occupies the store until that exact
    /// URL is fetched again — and invalidation matches a prefix, so the entries it leaves behind
    /// are the ones least likely to be refetched. See issue 68.
    /// <para>
    /// Queued writes are a different kind of record and are never in scope here, whatever their
    /// URL. That used to need a filter; now it needs nothing.
    /// </para>
    /// </remarks>
    Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Outbox
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns every queued write awaiting delivery, oldest first.
    /// </summary>
    /// <remarks>
    /// <b>Ordered by <see cref="QueuedWrite.CreatedUtc"/> ascending.</b> The ordering is the
    /// store's to provide — the processor does not sort, and delivery order is a promise
    /// Hyperwyc makes to its callers.
    /// </remarks>
    Task<IReadOnlyList<QueuedWrite>> GetPendingOutboxAsync(CancellationToken ct = default);

    /// <summary>
    /// Inserts or replaces <paramref name="write"/>, keyed on <see cref="QueuedWrite.Id"/>.
    /// </summary>
    /// <remarks>
    /// Both jobs are real: the handler inserts a write it has just taken custody of, and the
    /// processor replaces one whose <see cref="QueuedWrite.LastOutcome"/> has changed after a
    /// failed attempt. Insert or replace; never append.
    /// </remarks>
    Task UpsertQueuedWriteAsync(QueuedWrite write, CancellationToken ct = default);

    /// <summary>
    /// <b>Removes</b> the delivered write identified by <paramref name="id"/>, request and all.
    /// Tolerate an id that is not there.
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
    /// Only a transport failure leaves a write in the store — see
    /// <see cref="Models.DeliveryOutcomeKind"/>.
    /// </para>
    /// </remarks>
    Task RemoveDeliveredAsync(string id, CancellationToken ct = default);

    // -------------------------------------------------------------------------
    // Both
    // -------------------------------------------------------------------------

    /// <summary>
    /// Clears all data from the store — the outbox and the cache alike, and any store set aside
    /// by <see cref="TryQuarantineAsync"/>.
    /// </summary>
    /// <remarks>
    /// An explicit reset is the consumer saying <em>discard</em>, so it takes the quarantine with
    /// it. Leaving one behind would keep the previous user's writes on disk through the logout
    /// this method exists for, which is the opposite of what was asked.
    /// </remarks>
    Task ResetAsync(CancellationToken ct = default);

    /// <summary>
    /// Moves the unreadable store aside and starts a clean one, returning whether it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once per session, when a store operation has just failed. Returning
    /// <see langword="true"/> means the store is now empty and usable, and Hyperwyc resumes
    /// caching and queueing; returning <see langword="false"/> means it is not, and Hyperwyc
    /// reports <see cref="Models.HyperwycEventType.OnStoreUnreadable"/> and passes every request
    /// through for the rest of the session.
    /// </para>
    /// <para>
    /// <b>Move, do not delete.</b> The usual cause of an unreadable store is a key or path
    /// change rather than damage, so the bytes are generally intact and merely unopenable — and
    /// they are a user's queued writes. An implementation with nowhere to put them says so by
    /// returning <see langword="false"/>, which is what the default does.
    /// </para>
    /// <para>
    /// <b>Return <see langword="false"/> if a quarantined store already exists.</b> That is the
    /// whole of the bound on accumulation: a store that becomes unreadable twice is a systemic
    /// fault rather than an incident, and the second failure should surface rather than churn
    /// another copy onto the device. It also means the second orphan never overwrites the first,
    /// which is usually the more diagnostic one.
    /// </para>
    /// <para>
    /// Whatever is moved aside must stay readable by whoever holds the key — see
    /// <c>CabinetStore.OpenQuarantined</c> for the trap that lies in wait when the key is derived
    /// from the store's own path.
    /// </para>
    /// </remarks>
    Task<bool> TryQuarantineAsync(CancellationToken ct = default) => Task.FromResult(false);
}
