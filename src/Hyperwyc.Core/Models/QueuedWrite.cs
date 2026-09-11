namespace Hyperwyc.Models;

/// <summary>
/// A write Hyperwyc has taken custody of and has not yet delivered: everything needed to
/// replay the original request once the network is usable again.
/// </summary>
/// <remarks>
/// One of the two kinds of record in the store, and the reason there are two types rather than
/// one. They were a single <c>Envelope</c> told apart by a boolean, so every field had to be
/// read against a kind the type did not state — see
/// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/Backlog/Done/55-envelope-kind-discriminator.md">issue 55</see>
/// and <see cref="CachedResponse"/>.
/// </remarks>
public sealed class QueuedWrite
{
    /// <summary>
    /// Unique identifier for this write, assigned at construction time. Internal to
    /// Hyperwyc's own bookkeeping: it is not sent to the server and carries no meaning
    /// to the application's API.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// The application's identifier for this write, echoed on every <see cref="HyperwycEvent"/>
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
    /// The HTTP method of the request (e.g. "POST", "PUT", "DELETE").
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
    /// The request headers to be forwarded when the write is replayed.
    /// </summary>
    public Dictionary<string, string> RequestHeaders { get; init; }

    /// <summary>
    /// The request body as raw bytes, or <see langword="null"/> for bodyless requests.
    /// </summary>
    /// <remarks>
    /// Bytes rather than a string because a body is not necessarily text. Reading through
    /// <c>ReadAsStringAsync</c> decodes as UTF-8 and re-encodes on replay, which silently
    /// corrupts anything that is not valid UTF-8 text — a PNG upload, protobuf, a gzip-encoded
    /// payload. <see cref="GetRequestBodyAsText"/> covers the common case. See issue #25.
    /// <para>
    /// Named for the request rather than shortened to <c>Body</c> because
    /// <see cref="LastOutcome"/> carries one too, and the two appear together wherever an
    /// undelivered write is inspected.
    /// </para>
    /// </remarks>
    public byte[]? RequestBody { get; init; }

    /// <summary>
    /// The UTC time at which the write was queued. The outbox is drained in ascending order
    /// of it, which is the delivery-order promise Hyperwyc makes to its callers.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// The number of times Hyperwyc has attempted to deliver the request. Does NOT include
    /// the original attempt made by the caller.
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// What happened on the most recent delivery attempt, or <see langword="null"/> if none
    /// has been made.
    /// </summary>
    /// <remarks>
    /// In practice only ever a <see cref="DeliveryOutcomeKind.TransportFailure"/>. A delivered
    /// write is discarded, so there is nowhere for its outcome to live and no reason for
    /// Hyperwyc to hold one — the event carries it and the application keeps what it needs. See
    /// ADR 0010. What is left is the record that lets an outbox which is not draining explain
    /// itself after a restart, which is issue 23's read path.
    /// </remarks>
    public DeliveryOutcome? LastOutcome { get; set; }

    /// <summary>
    /// Initialises a new <see cref="QueuedWrite"/> with a generated <see cref="Id"/>
    /// and the current UTC time as <see cref="CreatedUtc"/>.
    /// </summary>
    public QueuedWrite()
    {
        Id = Guid.NewGuid().ToString();
        CorrelationId = Id;
        Url = string.Empty;
        Method = string.Empty;
        RequestHeaders = [];
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Captures an <see cref="HttpRequestMessage"/> for later replay. The request body is read
    /// synchronously if present; the caller should ensure the content is buffered beforehand.
    /// </summary>
    /// <param name="request">The HTTP request to capture.</param>
    /// <param name="clientName">
    /// The named client the request was made on, so a replay can return through the
    /// same pipeline.
    /// </param>
    /// <returns>A new <see cref="QueuedWrite"/> representing the pending request.</returns>
    public static QueuedWrite For(HttpRequestMessage request, string? clientName = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = Guid.NewGuid().ToString();
        var headers = HeaderMap.Flatten(request.Headers);

        byte[]? body = null;
        if (request.Content is not null)
        {
            body = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            foreach (var (key, value) in HeaderMap.Flatten(request.Content.Headers))
                headers[key] = value;
        }

        // The caller's own key if they set one, so they need no mapping table; otherwise
        // Hyperwyc's id, which the synthetic 202 hands back.
        var correlationId = request.Options.TryGetValue(HyperwycRequestOptions.CorrelationId, out var supplied)
            && !string.IsNullOrWhiteSpace(supplied)
                ? supplied
                : id;

        return new QueuedWrite
        {
            Id              = id,
            CorrelationId   = correlationId,
            Url             = request.RequestUri?.ToString() ?? string.Empty,
            Method          = request.Method.Method,
            ClientName      = clientName,
            RequestHeaders  = headers,
            RequestBody     = body,
        };
    }

    /// <summary>
    /// <see cref="RequestBody"/> decoded as UTF-8, or <see langword="null"/> if there is no body.
    /// </summary>
    /// <remarks>
    /// For diagnostics and for callers who know the route is textual. Assumes UTF-8 rather than
    /// consulting the captured <c>Content-Type</c>; a caller needing anything else has the raw
    /// bytes.
    /// </remarks>
    public string? GetRequestBodyAsText() =>
        RequestBody is null ? null : System.Text.Encoding.UTF8.GetString(RequestBody);
}
