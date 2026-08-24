using System.Net.Http.Headers;

namespace Hyperwyc.Models;

/// <summary>
/// Represents a request/response pair persisted by Hyperwyc.
/// Covers both outbox entries (pending writes awaiting connectivity) and
/// cache entries (GET responses stored for offline reads).
/// </summary>
public sealed class Envelope
{
    /// <summary>
    /// Unique identifier for this envelope, assigned at construction time. Internal to
    /// Hyperwyc's own bookkeeping: it is not sent to the server and carries no meaning
    /// to the application's API.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// The application's identifier for this write, echoed on every <see cref="SyncEvent"/>
    /// concerning it so a deferred outcome can be matched back to the record that produced it.
    /// </summary>
    /// <remarks>
    /// Taken from <see cref="HyperwycRequestOptions.CorrelationId"/> when the caller set one,
    /// and otherwise defaulted to <see cref="Id"/>. Kept as a distinct field because it is
    /// under the application's control and carries no uniqueness guarantee, whereas
    /// <see cref="Id"/> keys the store.
    /// </remarks>
    public string CorrelationId { get; init; }

    /// <summary>
    /// The full URL of the request.
    /// </summary>
    public string Url { get; init; }

    /// <summary>
    /// The HTTP method of the request (e.g. "GET", "POST", "PUT").
    /// </summary>
    public string Method { get; init; }

    /// <summary>
    /// The name of the <see cref="HttpClient"/> this request was made on, used to
    /// replay it through the same pipeline — and therefore the same auth, logging and
    /// telemetry handlers. <see langword="null"/> when the handler was registered
    /// without a name.
    /// </summary>
    public string? ClientName { get; init; }

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
    /// The UTC time at which this envelope was created.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// The cached response associated with this envelope, populated when a
    /// successful response has been stored for offline reads.
    /// </summary>
    public CachedResponse? Response { get; set; }

    /// <summary>
    /// What happened on the most recent delivery attempt, or <see langword="null"/> if none
    /// has been made.
    /// </summary>
    /// <remarks>
    /// Persisted so a dead-lettered envelope can still explain itself after a restart, which
    /// an event cannot. Only failures are retained: a successful envelope leaves the outbox,
    /// so there is nowhere for its outcome to live — see issue 40.
    /// </remarks>
    public SyncOutcome? LastOutcome { get; set; }

    /// <summary>
    /// Initialises a new <see cref="Envelope"/> with a generated <see cref="Id"/>
    /// and the current UTC time as <see cref="CreatedUtc"/>.
    /// </summary>
    public Envelope()
    {
        Id = Guid.NewGuid().ToString();
        CorrelationId = Id;
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
    /// <param name="clientName">
    /// The named client the request was made on, so a replay can return through the
    /// same pipeline.
    /// </param>
    public static Envelope ForRequest(HttpRequestMessage request, string? clientName = null)
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

        var id = Guid.NewGuid().ToString();

        // The caller's own key if they set one, so they need no mapping table; otherwise
        // Hyperwyc's id, which the synthetic 202 hands back.
        var correlationId = request.Options.TryGetValue(HyperwycRequestOptions.CorrelationId, out var supplied)
            && !string.IsNullOrWhiteSpace(supplied)
                ? supplied
                : id;

        return new Envelope
        {
            Id = id,
            CorrelationId = correlationId,
            Url = request.RequestUri?.ToString() ?? string.Empty,
            Method = request.Method.Method,
            ClientName = clientName,
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
    /// <param name="clientName">The named client the request was made on.</param>
    /// <returns>
    /// A new <see cref="Envelope"/> with <see cref="Response"/> populated and
    /// <see cref="IsSynced"/> set to <c>true</c>.
    /// </returns>
    public static Envelope ForCachedResponse(
        HttpRequestMessage request,
        HttpResponseMessage response,
        string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var envelope = ForRequest(request, clientName);

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
