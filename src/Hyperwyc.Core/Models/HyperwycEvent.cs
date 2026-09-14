namespace Hyperwyc.Models;

/// <summary>
/// An event emitted by Hyperwyc as requests move through their sync lifecycle.
/// Subscribe via <see cref="Interfaces.IHyperwyc.Events"/>.
/// </summary>
/// <remarks>
/// <para>
/// Events are transient, and for a delivery this is the <b>only</b> report there is: a delivered
/// write leaves the store along with what the server said about it, because keeping an ordinary
/// HTTP response is the application's business rather than Hyperwyc's. See
/// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/docs/decisions/0010-delivery-ends-hyperwycs-interest.md">ADR 0010</see>.
/// The practical consequence is that a consumer has to be subscribed before a flush runs, which
/// is why <see cref="HyperwycOptions.FlushOnStartup"/> defaults to <see langword="false"/>.
/// </para>
/// <para>
/// The one thing that is persisted rather than published is a transport failure, which raises no
/// event at all — the envelope stays queued and carries its own account of why. See
/// <see cref="QueuedWrite.LastOutcome"/>.
/// </para>
/// </remarks>
public record HyperwycEvent
{
    /// <summary>The kind of lifecycle transition that occurred.</summary>
    public required HyperwycEventType Type { get; init; }

    /// <summary>The URL of the request that triggered the event.</summary>
    public required string Url { get; init; }

    /// <summary>The HTTP method of the request (e.g. <c>GET</c>, <c>POST</c>).</summary>
    public required string Method { get; init; }

    /// <summary>The UTC instant at which the event was emitted.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Identifies which queued write this concerns. The value the caller set through
    /// <see cref="HyperwycRequestOptions.CorrelationId"/>, or one Hyperwyc generated and returned
    /// on the <c>202</c>. <see langword="null"/> for events that do not concern a queued write,
    /// such as <see cref="HyperwycEventType.OnUpdated"/>.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Hyperwyc's own identifier for the envelope. Unique, unlike
    /// <see cref="CorrelationId"/>, and the handle a diagnostics view over the outbox uses.
    /// Applications correlating against their own records want <see cref="CorrelationId"/>.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// The body of the queued request as raw bytes, so a consumer can deserialise its own payload
    /// back out without having kept a copy. <see langword="null"/> for bodyless requests. Bytes
    /// rather than a string for the same reason as <see cref="QueuedWrite.RequestBody"/> — a body is
    /// not necessarily text.
    /// </summary>
    public byte[]? RequestBody { get; init; }

    /// <summary>
    /// What the server said, on <see cref="HyperwycEventType.OnDelivered"/> — the status, the reason
    /// phrase and the body, whatever they were. <see langword="null"/> on every other event type,
    /// where no delivery has completed.
    /// </summary>
    public DeliveryOutcome? Outcome { get; init; }

    /// <summary>
    /// <see cref="RequestBody"/> decoded as UTF-8, or <see langword="null"/> if there is no body.
    /// </summary>
    /// <remarks>
    /// The convenience for a textual route, matching <see cref="QueuedWrite.GetRequestBodyAsText"/>,
    /// <see cref="CachedResponse.GetBodyAsText"/> and <see cref="DeliveryOutcome.GetBodyAsText"/>.
    /// A caller whose payload is not text has the raw bytes.
    /// </remarks>
    public string? GetRequestBodyAsText() =>
        RequestBody is null ? null : System.Text.Encoding.UTF8.GetString(RequestBody);
}
