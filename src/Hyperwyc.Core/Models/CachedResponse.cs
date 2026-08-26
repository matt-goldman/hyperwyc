namespace Hyperwyc.Models;

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
    /// The response body as raw bytes, or <see langword="null"/> if the response had no body.
    /// </summary>
    /// <remarks>
    /// Bytes rather than a string so an image, a protobuf payload or a gzip-encoded body
    /// survives caching byte for byte. <see cref="GetBodyAsText"/> covers the common case.
    /// See issue #25.
    /// </remarks>
    public byte[]? Body { get; init; }

    /// <summary>
    /// The UTC time at which this response was cached.
    /// </summary>
    public DateTimeOffset CachedAt { get; init; }

    /// <summary>
    /// <see cref="Body"/> decoded as UTF-8, or <see langword="null"/> if there is no body.
    /// </summary>
    /// <remarks>
    /// For diagnostics and for callers who know the route is textual. The served response
    /// carries the original <c>Content-Type</c>, so ordinary consumers never need this.
    /// </remarks>
    public string? GetBodyAsText() =>
        Body is null ? null : System.Text.Encoding.UTF8.GetString(Body);
}
