namespace Hyperwyc.Models;

/// <summary>
/// A read-only projection of a <see cref="QueuedWrite"/> exposed through
/// <see cref="Interfaces.IHyperwycDiagnostics"/>. Carries what an operator needs to explain
/// an outbox that will not drain, and nothing else — the request body and headers stay on the
/// store record, since they are the largest and most sensitive things Hyperwyc holds.
/// </summary>
/// <remarks>
/// Properties rather than a positional record, so that a field can be added without changing the
/// constructor or <c>Deconstruct</c> signature every consumer compiled against (issue #73).
/// </remarks>
public record PendingItem
{
    /// <summary>
    /// Hyperwyc's unique internal identifier for the item. Matches
    /// <see cref="HyperwycEvent.RequestId"/>, so a pending item can be correlated with any events
    /// it produces.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// A correlation ID for tracing; the application's own value if it set one on
    /// <see cref="HyperwycRequestOptions.CorrelationId"/>, otherwise <see cref="Id"/>.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>The HTTP method of the request (e.g. "POST", "PUT", "DELETE").</summary>
    public required string Method { get; init; }

    /// <summary>The full URL the request was made to.</summary>
    public required string Url { get; init; }

    /// <summary>
    /// UTC time the write was originally queued. Not updated on retries.
    /// </summary>
    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// The number of times Hyperwyc has attempted to deliver the request. Does not include the
    /// original attempt made by the calling code.
    /// </summary>
    public required int RetryCount { get; init; }

    /// <summary>
    /// The <see cref="DeliveryOutcome"/> of the most recent attempt, or <see langword="null"/> if
    /// Hyperwyc has not yet attempted delivery. In practice always a
    /// <see cref="DeliveryOutcomeKind.TransportFailure"/> when present — a delivered write is
    /// removed from the store, so its outcome lives only on the event stream.
    /// </summary>
    public DeliveryOutcome? LastOutcome { get; init; }

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

        return new PendingItem
        {
            Id            = write.Id,
            CorrelationId = write.CorrelationId,
            Method        = write.Method,
            Url           = write.Url,
            CreatedUtc    = write.CreatedUtc,
            RetryCount    = write.RetryCount,
            LastOutcome   = write.LastOutcome,
        };
    }
}
