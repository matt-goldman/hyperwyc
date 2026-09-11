using Hyperwyc.Models;

namespace Hyperwyc.Interfaces;

/// <summary>
/// Provides a diagnostic view of pending items in the outbox.
/// </summary>
public interface IHyperwycDiagnostics
{
    /// <summary>
    /// Gets a diagnostic view of all pending <see cref="QueuedWrite"/>s in the outbox.
    /// </summary>
    /// <param name="ct">A cancellation token used to terminate a request in flight</param>
    /// <returns>A read only list of <see cref="PendingItem"/></returns>
    Task<IReadOnlyList<PendingItem>> GetPendingOutboxAsync(CancellationToken ct = default);
}
