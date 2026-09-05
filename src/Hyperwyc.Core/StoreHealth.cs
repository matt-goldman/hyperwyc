using Hyperwyc.Models;
using Microsoft.Extensions.Logging;

namespace Hyperwyc;

/// <summary>
/// Tracks whether Hyperwyc's store can be read, and reports the first failure.
/// </summary>
/// <remarks>
/// <para>
/// Infrastructure rather than something a consumer configures — public only because
/// <see cref="HyperwycHandler"/>'s constructor is, and takes it. Resolve it from the container
/// if you want to ask; do not construct one.
/// </para>
/// <para>
/// A store that cannot be read is a fact to report, not a problem to solve. Hyperwyc is a
/// transport-level component: it does not guarantee delivery, and it has no standing to decide
/// what a damaged store is worth. So this neither deletes anything nor throws. It logs, publishes
/// one event, and lets everything downstream degrade to behaving as though Hyperwyc were not
/// installed. See issue #49.
/// </para>
/// <para>
/// Shared state rather than a flag on the handler, because <see cref="HyperwycHandler"/> is
/// transient and <see cref="OutboxProcessor"/> is a singleton, and they have to agree. It is also
/// load-bearing rather than merely a log-volume optimisation: the handler consults
/// <see cref="IsUsable"/> to decide whether it may still take custody of a write.
/// </para>
/// </remarks>
public sealed class StoreHealth(HyperwycEventStream events, ILogger<StoreHealth>? logger = null)
{
    private readonly Lock _gate = new();
    private bool _reported;

    /// <summary>Whether the store has read successfully, or at least not yet failed.</summary>
    /// <remarks>
    /// Latches false on the first failure and stays there until <see cref="Recovered"/>. Not
    /// retried: whatever made the store unreadable — a wrong key, a shape that no longer
    /// deserialises — does not fix itself between one request and the next, and retrying would
    /// pay the cost on every call to learn the same thing.
    /// </remarks>
    public bool IsUsable { get; private set; } = true;

    /// <summary>
    /// Records that the store could not be read, logging and publishing once.
    /// </summary>
    /// <param name="error">What the store threw.</param>
    /// <param name="usingDerivedKey">
    /// Whether the store is protected by a key Hyperwyc derived rather than one the consumer
    /// supplied. Changes only the wording of the log line — the behaviour is identical either
    /// way, because both cases leave Hyperwyc with nothing to read and neither entitles it to
    /// destroy anything.
    /// </param>
    public void ReportUnreadable(Exception error, bool usingDerivedKey)
    {
        lock (_gate)
        {
            IsUsable = false;
            if (_reported) return;
            _reported = true;
        }

        logger?.LogError(
            error,
            "Hyperwyc's store could not be read, so caching and queueing are disabled for the "
            + "rest of this session; requests pass straight through. {Cause} Call "
            + "IHyperwyc.ResetStoreAsync() to discard the store and start again.",
            usingDerivedKey
                ? "The store is protected by a derived key, which usually means the store "
                  + "directory moved, or its persisted shape changed."
                : "The supplied encryption key does not match this store.");

        events.Publish(new HyperwycEvent(
            HyperwycEventType.OnStoreUnreadable,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    /// <summary>Marks the store usable again, after it has been cleared.</summary>
    public void Recovered()
    {
        lock (_gate)
        {
            IsUsable = true;
            _reported = false;
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
}
