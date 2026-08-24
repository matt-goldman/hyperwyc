namespace Hyperwyc.Models;

/// <summary>
/// An event emitted by Hyperwyc as requests move through their sync lifecycle.
/// Subscribe via <see cref="Interfaces.IHyperwyc.SyncEvents"/>.
/// </summary>
/// <remarks>
/// <para>
/// Events are transient. A background flush can complete while the application is not
/// running, so anything a consumer must not miss is also persisted on the envelope — see
/// <see cref="SyncOutcome"/>. Treat the event as the live notification and the store as the
/// record of truth.
/// </para>
/// </remarks>
/// <param name="Type">The kind of lifecycle transition that occurred.</param>
/// <param name="Url">The URL of the request that triggered the event.</param>
/// <param name="Method">The HTTP method of the request (e.g. <c>GET</c>, <c>POST</c>).</param>
/// <param name="Timestamp">The UTC instant at which the event was emitted.</param>
/// <param name="CorrelationId">
/// Identifies which queued write this concerns. The value the caller set through
/// <see cref="HyperwycRequestOptions.CorrelationId"/>, or one Hyperwyc generated and returned
/// on the <c>202</c>. <see langword="null"/> for events that do not concern a queued write,
/// such as <see cref="SyncEventType.OnUpdated"/>.
/// </param>
/// <param name="RequestId">
/// Hyperwyc's own identifier for the envelope. Unique, unlike
/// <paramref name="CorrelationId"/>, and the handle a diagnostics or dead-letter view uses.
/// Applications correlating against their own records want <paramref name="CorrelationId"/>.
/// </param>
/// <param name="RequestBody">
/// The body of the queued request, so a consumer can deserialise its own payload back out
/// without having kept a copy. <see langword="null"/> for bodyless requests.
/// </param>
/// <param name="Outcome">
/// What the server or network said, for events that report a delivery attempt
/// (<see cref="SyncEventType.OnSynced"/>, <see cref="SyncEventType.OnFailed"/>).
/// <see langword="null"/> on
/// <see cref="SyncEventType.OnQueued"/> and <see cref="SyncEventType.OnUpdated"/>, where no
/// attempt has been made.
/// </param>
public record SyncEvent(
    SyncEventType Type,
    string Url,
    string Method,
    DateTimeOffset Timestamp,
    string? CorrelationId = null,
    string? RequestId = null,
    string? RequestBody = null,
    SyncOutcome? Outcome = null);
