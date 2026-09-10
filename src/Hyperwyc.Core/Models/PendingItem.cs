namespace Hyperwyc.Models;

/// <summary>
/// A read-only projection of a <see cref="QueuedWrite"/> exposed through
/// <see cref="Interfaces.IHyperwycDiagnostics"/>. Carries what an operator needs to explain
/// an outbox that will not drain, and nothing else — the request body and headers stay on the
/// store record, since they are the largest and most sensitive things Hyperwyc holds.
/// </summary>
/// <param name="Id">Hyperwyc's unique internal identifier for the item. Matches
/// <see cref="HyperwycEvent.RequestId"/>, so a pending item can be correlated with any events
/// it produces.</param>
/// <param name="CorrelationId">
/// A correlation ID for tracing; the application's own value if it set one on
/// <see cref="HyperwycRequestOptions.CorrelationId"/>, otherwise <see cref="Id"/>.
/// </param>
/// <param name="Method">The HTTP method of the request (e.g. "POST", "PUT", "DELETE").</param>
/// <param name="Url">The full URL the request was made to.</param>
/// <param name="CreatedUtc">
/// UTC time the write was originally queued. Not updated on retries.
/// </param>
/// <param name="RetryCount">
/// The number of times Hyperwyc has attempted to deliver the request. Does not include the
/// original attempt made by the calling code.
/// </param>
/// <param name="LastOutcome">
/// The <see cref="DeliveryOutcome"/> of the most recent attempt, or <see langword="null"/> if
/// Hyperwyc has not yet attempted delivery. In practice always a
/// <see cref="DeliveryOutcomeKind.TransportFailure"/> when present — a delivered write is
/// removed from the store, so its outcome lives only on the event stream.
/// </param>
public record PendingItem(
    string Id,
    string CorrelationId,
    string Method,
    string Url,
    DateTimeOffset CreatedUtc,
    int RetryCount,
    DeliveryOutcome? LastOutcome)
{
    /// <summary>
    /// Projects a <see cref="QueuedWrite"/> into its diagnostic view.
    /// </summary>
    /// <remarks>
    /// Lives here rather than on <see cref="QueuedWrite"/> so the store record does not need to
    /// know about its diagnostic projection.
    /// </remarks>
    public static PendingItem From(QueuedWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);

        return new PendingItem(
            write.Id,
            write.CorrelationId,
            write.Method,
            write.Url,
            write.CreatedUtc,
            write.RetryCount,
            write.LastOutcome);
    }
}
