namespace Restyc.Models;

/// <summary>
/// Represents a cached HTTP response associated with an <see cref="Envelope"/>.
/// Populated when a successful response is stored for offline reads.
/// </summary>
public sealed class CachedResponse
{
    /// <summary>
    /// The HTTP status code of the response (e.g. 200, 404).
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// The response headers returned by the server.
    /// </summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>
    /// The serialised response body, or <c>null</c> if the response had no body.
    /// Binary bodies are out of scope for v0.1; string representation only.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>
    /// The UTC time at which this response was cached.
    /// </summary>
    public DateTimeOffset CachedAt { get; init; }
}
