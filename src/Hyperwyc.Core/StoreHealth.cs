using Hyperwyc.Interfaces;
using Hyperwyc.Models;
using Microsoft.Extensions.Logging;

namespace Hyperwyc;

/// <summary>
/// Tracks whether Hyperwyc's store can be read, sets an unreadable one aside, and reports what
/// it could not recover from.
/// </summary>
/// <remarks>
/// <para>
/// Infrastructure rather than something a consumer configures — public only because
/// <see cref="HyperwycHandler"/>'s constructor is, and takes it. Resolve it from the container
/// if you want to ask; do not construct one.
/// </para>
/// <para>
/// A store that cannot be read is <b>moved aside once</b> and replaced with a clean one, so
/// caching and queueing resume rather than being off for the session. Nothing is deleted: the
/// usual cause is a key or path change rather than damage, so the bytes are generally intact and
/// merely unopenable, and they are a user's queued writes. See issue 62.
/// </para>
/// <para>
/// When that is not possible — a store with nowhere to put them, or one that has already been
/// quarantined this session — the original behaviour stands: log, publish one event, and let
/// everything downstream degrade to behaving as though Hyperwyc were not installed. Nothing is
/// thrown either way. See issue #49.
/// </para>
/// <para>
/// Shared state rather than a flag on the handler, because <see cref="HyperwycHandler"/> is
/// transient and <see cref="OutboxProcessor"/> is a singleton, and they have to agree. It is also
/// load-bearing rather than merely a log-volume optimisation: the handler consults
/// <see cref="IsUsable"/> to decide whether it may still take custody of a write.
/// </para>
/// </remarks>
/// <param name="store">The store to quarantine, if it turns out to need it.</param>
/// <param name="options">
/// Consulted only for <see cref="HyperwycOptions.UsesDerivedEncryptionKey"/>, which changes the
/// wording of the log line. Held here rather than passed on every report, because it is a fact
/// about the configuration and does not vary by call site.
/// </param>
/// <param name="events">The stream lifecycle events are published on.</param>
/// <param name="logger">Optional: Hyperwyc must work in a container that has no logging.</param>
public sealed class StoreHealth(
    IHyperwycStore store,
    HyperwycOptions options,
    HyperwycEventStream events,
    ILogger<StoreHealth>? logger = null)
{
    private readonly Lock _gate = new();

    // Async, so it cannot be the lock above: quarantining is a store operation. It serialises
    // concurrent reports so that exactly one of them attempts recovery and the rest wait for the
    // answer rather than racing to conclude the session is over.
    private readonly SemaphoreSlim _recovery = new(1, 1);

    private bool _reported;
    private bool _quarantineAttempted;
    private int _generation;

    /// <summary>Whether the store has read successfully, or at least not yet failed.</summary>
    /// <remarks>
    /// Latches false on the first failure Hyperwyc could not recover from, and stays there until
    /// <see cref="Recovered"/>. Not retried: whatever made the store unreadable — a wrong key, a
    /// shape that no longer deserialises — does not fix itself between one request and the next,
    /// and retrying would pay the cost on every call to learn the same thing.
    /// </remarks>
    public bool IsUsable { get; private set; } = true;

    /// <summary>
    /// Identifies the store contents a caller is about to read or write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Changes whenever the store is emptied — by a quarantine, or by an explicit reset. Read it
    /// <b>before</b> a store operation and hand it back to
    /// <see cref="ReportUnreadableAsync"/> if that operation fails, so a failure against contents
    /// that have since been set aside is recognised as stale and discarded.
    /// </para>
    /// <para>
    /// Without it the common case breaks the feature: two requests in flight both hit the broken
    /// store and both throw, the first quarantines and recovers, and the second — reporting a
    /// failure that is already history — would latch the session off a moment later.
    /// </para>
    /// </remarks>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Records that a store operation failed: sets the store aside and starts a clean one if it
    /// can, and otherwise logs and publishes once.
    /// </summary>
    /// <param name="error">What the store threw.</param>
    /// <param name="generation">
    /// The value of <see cref="Generation"/> read before the operation that failed. A report
    /// carrying a stale generation is about contents that have already been replaced, and is
    /// dropped.
    /// </param>
    /// <param name="ct">Cancels the quarantine attempt.</param>
    public async Task ReportUnreadableAsync(Exception error, int generation, CancellationToken ct = default)
    {
        // Before the await, and before the quarantine attempt: the store is known broken from
        // here, and nothing should start a new operation against it in the meantime.
        lock (_gate)
        {
            if (generation != _generation) return;
            IsUsable = false;
        }

        await _recovery.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked under the recovery gate. A caller that queued behind the one which
            // actually recovered is reporting a failure against contents that are gone.
            if (generation != Generation) return;

            if (!_quarantineAttempted)
            {
                _quarantineAttempted = true;
                if (await TryQuarantineAsync(ct).ConfigureAwait(false)) return;
            }

            Report(error);
        }
        finally
        {
            _recovery.Release();
        }
    }

    /// <summary>Marks the store usable again, after it has been cleared.</summary>
    /// <remarks>
    /// Also allows a quarantine again. An explicit reset discards everything including the
    /// previous quarantine, so the counter that bounds accumulation has been reset with it.
    /// </remarks>
    public void Recovered()
    {
        lock (_gate)
        {
            IsUsable = true;
            _reported = false;
            _quarantineAttempted = false;
            _generation++;
        }
    }

    /// <summary>
    /// Whether <paramref name="error"/> means the store could not be read, as opposed to the
    /// caller having given up.
    /// </summary>
    /// <remarks>
    /// Everything except cancellation. Deliberately broad: a store is a consumer-supplied
    /// implementation and the interesting failures have already included
    /// <c>CryptographicException</c> from a key mismatch and <c>JsonException</c> from a
    /// persisted shape that changed. Enumerating the ones seen so far would only mean the next
    /// unfamiliar one escapes into an application's HTTP call, which is the thing this exists to
    /// prevent.
    /// </remarks>
    public static bool IsStoreFailure(Exception error) => error is not OperationCanceledException;

    // -------------------------------------------------------------------------
    // Recovery
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asks the store to set itself aside, and reports the outcome. Called at most once per
    /// generation, under <see cref="_recovery"/>.
    /// </summary>
    /// <remarks>
    /// A store that throws here is treated exactly as one that declined. This is reached from a
    /// <c>catch</c> block on the caller's own HTTP request, so an exception escaping would
    /// surface out of <c>HttpClient.SendAsync</c> — the defect issue #49 exists to prevent, in
    /// the one place least likely to be looked at.
    /// </remarks>
    private async Task<bool> TryQuarantineAsync(CancellationToken ct)
    {
        bool quarantined;
        try
        {
            quarantined = await store.TryQuarantineAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            logger?.LogDebug(ex, "Hyperwyc could not set the unreadable store aside.");
            return false;
        }

        if (!quarantined) return false;

        // Not a warning. Nothing is broken from the application's point of view: caching and
        // queueing carry on against an empty store, which is what they would have done if the
        // device had never held one.
        logger?.LogInformation(
            "Hyperwyc's store could not be read, so it has been set aside and a clean one started. "
            + "Caching and queueing continue, against an empty store. Nothing was deleted: the "
            + "previous contents were moved rather than discarded, and are still readable with the "
            + "key they were written under. {Cause}",
            CauseText);

        events.Publish(new HyperwycEvent(
            HyperwycEventType.OnStoreQuarantined,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow));

        lock (_gate)
        {
            IsUsable = true;
            _reported = false;
            _generation++;
        }

        return true;
    }

    /// <summary>
    /// Logs and publishes the failure, once, leaving the store latched unusable.
    /// </summary>
    private void Report(Exception error)
    {
        lock (_gate)
        {
            if (_reported) return;
            _reported = true;
        }

        logger?.LogError(
            error,
            "Hyperwyc's store could not be read, so caching and queueing are disabled for the "
            + "rest of this session; requests pass straight through. {Cause} A store that has "
            + "already been set aside once this session is not set aside again — a store that "
            + "becomes unreadable twice is a systemic fault rather than an incident. Call "
            + "IHyperwyc.ResetStoreAsync() to discard the store and start again.",
            CauseText);

        events.Publish(new HyperwycEvent(
            HyperwycEventType.OnStoreUnreadable,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The half of the log line that differs by whose key protects the store.
    /// </summary>
    /// <remarks>
    /// Wording only — the behaviour is identical either way, because both cases leave Hyperwyc
    /// with nothing to read and neither entitles it to destroy anything.
    /// </remarks>
    private string CauseText =>
        options.UsesDerivedEncryptionKey
            ? "The store is protected by a derived key, which usually means the store directory "
              + "moved, or its persisted shape changed."
            : "The supplied encryption key does not match this store.";
}
