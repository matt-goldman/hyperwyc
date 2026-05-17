using System.Net.Http.Headers;

namespace hyperwyc.Models;

/// <summary>
/// Represents a request/response pair persisted by hyperwyc.
/// Covers both outbox entries (pending writes awaiting connectivity) and
/// cache entries (GET responses stored for offline reads).
/// </summary>
public sealed class Envelope
{
    /// <summary>
    /// Unique identifier for this envelope, also used as the <c>Idempotency-Key</c>
    /// header value when the request is (re)sent. Assigned at construction time.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// The full URL of the request.
    /// </summary>
    public string Url { get; init; }

    /// <summary>
    /// The HTTP method of the request (e.g. "GET", "POST", "PUT").
    /// </summary>
    public string Method { get; init; }

    /// <summary>
    /// The request headers to be forwarded with the request.
    /// </summary>
    public Dictionary<string, string> RequestHeaders { get; init; }

    /// <summary>
    /// The serialised request body, or <c>null</c> for bodyless requests.
    /// Binary bodies are out of scope for v0.1; string representation only.
    /// </summary>
    public string? RequestBody { get; init; }

    /// <summary>
    /// Indicates whether the request has been successfully synchronised
    /// (sent to the remote server) at least once.
    /// </summary>
    public bool IsSynced { get; set; }

    /// <summary>
    /// Indicates whether the envelope has been moved to the dead-letter store
    /// after exhausting all retry attempts.
    /// </summary>
    public bool IsDeadLettered { get; set; }

    /// <summary>
    /// The number of times the request has been retried after an initial failure.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// The UTC time at which the next retry should be attempted,
    /// or <c>null</c> if no retry is currently scheduled.
    /// </summary>
    public DateTimeOffset? NextRetryUtc { get; set; }

    /// <summary>
    /// The UTC time at which this envelope was created.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// The cached response associated with this envelope, populated when a
    /// successful response has been stored for offline reads.
    /// </summary>
    public CachedResponse? Response { get; set; }

    /// <summary>
    /// Initialises a new <see cref="Envelope"/> with a generated <see cref="Id"/>
    /// and the current UTC time as <see cref="CreatedUtc"/>.
    /// </summary>
    public Envelope()
    {
        Id = Guid.NewGuid().ToString();
        Url = string.Empty;
        Method = string.Empty;
        RequestHeaders = [];
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Creates an unsent outbox <see cref="Envelope"/> from an
    /// <see cref="HttpRequestMessage"/>. The request body is read synchronously
    /// if present; the caller should ensure the content is buffered beforehand.
    /// </summary>
    /// <param name="request">The HTTP request to wrap.</param>
    /// <returns>A new <see cref="Envelope"/> representing the pending request.</returns>
    public static Envelope ForRequest(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var headers = FlattenHeaders(request.Headers);

        string? body = null;
        if (request.Content is not null)
        {
            body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            foreach (var (key, value) in FlattenHeaders(request.Content.Headers))
                headers[key] = value;
        }

        // Honour a pre-set Idempotency-Key so that replayed requests carry the same Id.
        var idempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var keyValues)
            ? keyValues.First()
            : null;

        return new Envelope
        {
            Id = idempotencyKey ?? Guid.NewGuid().ToString(),
            Url = request.RequestUri?.ToString() ?? string.Empty,
            Method = request.Method.Method,
            RequestHeaders = headers,
            RequestBody = body,
        };
    }

    /// <summary>
    /// Creates a cache <see cref="Envelope"/> from a completed
    /// <see cref="HttpRequestMessage"/> / <see cref="HttpResponseMessage"/> pair.
    /// The response body is read synchronously; the caller should ensure the
    /// content is buffered beforehand.
    /// </summary>
    /// <param name="request">The original HTTP request.</param>
    /// <param name="response">The HTTP response to cache.</param>
    /// <returns>
    /// A new <see cref="Envelope"/> with <see cref="Response"/> populated and
    /// <see cref="IsSynced"/> set to <c>true</c>.
    /// </returns>
    public static Envelope ForCachedResponse(HttpRequestMessage request, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var envelope = ForRequest(request);

        var responseHeaders = FlattenHeaders(response.Headers);

        string? responseBody = null;
        if (response.Content is not null)
        {
            responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            foreach (var (key, value) in FlattenHeaders(response.Content.Headers))
                responseHeaders[key] = value;
        }

        envelope.IsSynced = true;
        envelope.Response = new CachedResponse
        {
            StatusCode = (int)response.StatusCode,
            Headers = responseHeaders,
            Body = responseBody,
            CachedAt = DateTimeOffset.UtcNow,
        };

        return envelope;
    }

    private static Dictionary<string, string> FlattenHeaders(HttpHeaders headers) =>
        headers.ToDictionary(
            h => h.Key,
            h => string.Join(", ", h.Value),
            StringComparer.OrdinalIgnoreCase);
}
