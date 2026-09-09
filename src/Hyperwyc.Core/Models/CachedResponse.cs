namespace Hyperwyc.Models;

/// <summary>
/// A response stored against the URL that produced it, to be served when the network cannot
/// answer — or, under <see cref="SourcePriority.CacheFirst"/>, in preference to asking it.
/// </summary>
/// <remarks>
/// <para>
/// One of the two kinds of record in the store, and the reason there are two types rather than
/// one. They were a single <c>Envelope</c> told apart by a boolean, so a reader could not tell
/// which fields were load-bearing without knowing a kind the type did not state — see
/// <see href="https://github.com/mattgoldman/hyperwyc/blob/main/Backlog/Done/55-envelope-kind-discriminator.md">issue 55</see>
/// and <see cref="QueuedWrite"/>.
/// </para>
/// <para>
/// Splitting the types took the request's method, headers, body, client name and correlation id
/// off this one. Nothing read them on a cached response, and they were the store's largest and
/// most sensitive holding — captured request headers include whatever the caller sent, an
/// <c>Authorization</c> value among them. See ADR 0008.
/// </para>
/// </remarks>
public sealed class CachedResponse
{
    /// <summary>
    /// The full URL this response was fetched from, and the identity of the entry.
    /// </summary>
    /// <remarks>
    /// A cache entry is identified by what it caches, so the store keys on this rather than on
    /// a generated id. That is what makes <c>IHyperwycStore.PutCachedResponseAsync</c>
    /// <em>replace</em> the previous response for a URL instead of adding a second one beside
    /// it.
    /// <para>
    /// It did add one beside it, and the consequences were worse than a stale read.
    /// <c>GetCachedResponseAsync</c> returns the first entry matching the URL, which is
    /// whichever the store happens to enumerate first — in practice the oldest — so the cache
    /// froze at the first response ever stored and never updated again, however many times the
    /// application refetched. Every refetch also left another record behind that nothing would
    /// ever read.
    /// </para>
    /// <para>
    /// Honouring <c>Vary</c> or a cache generation would extend this key, in one place.
    /// </para>
    /// </remarks>
    public string Url { get; init; } = string.Empty;

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
    /// The UTC time at which this response was cached, and the instant a route's
    /// <see cref="RoutePolicy.Ttl"/> is measured from.
    /// </summary>
    public DateTimeOffset CachedAt { get; init; }

    /// <summary>
    /// Captures a completed <see cref="HttpResponseMessage"/> for <paramref name="url"/>. The
    /// response body is read synchronously; the caller should ensure the content is buffered
    /// beforehand.
    /// </summary>
    /// <param name="url">The URL the response was fetched from.</param>
    /// <param name="response">The HTTP response to cache.</param>
    /// <remarks>
    /// Takes the URL rather than the <see cref="HttpRequestMessage"/> deliberately: the URL and
    /// the response are the whole of what a cache entry is, and a factory holding the request
    /// would invite the request fields back onto the type.
    /// </remarks>
    public static CachedResponse For(string url, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(response);

        var headers = HeaderMap.Flatten(response.Headers);

        var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

        foreach (var (key, value) in HeaderMap.Flatten(response.Content.Headers))
            headers[key] = value;

        return new CachedResponse
        {
            Url         = url,
            StatusCode  = (int)response.StatusCode,
            Headers     = headers,
            Body        = body,
            CachedAt    = DateTimeOffset.UtcNow,
        };
    }

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
