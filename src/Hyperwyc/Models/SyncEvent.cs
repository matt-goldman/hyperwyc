namespace hyperwyc.Models;

/// <summary>
/// An event emitted by hyperwyc as requests move through their sync lifecycle.
/// Subscribe via <see cref="Interfaces.Ihyperwyc.SyncEvents"/>.
/// </summary>
/// <param name="Type">The kind of lifecycle transition that occurred.</param>
/// <param name="Url">The URL of the request that triggered the event.</param>
/// <param name="Method">The HTTP method of the request (e.g. <c>GET</c>, <c>POST</c>).</param>
/// <param name="Timestamp">The UTC instant at which the event was emitted.</param>
public record SyncEvent(
    SyncEventType Type,
    string Url,
    string Method,
    DateTimeOffset Timestamp);
