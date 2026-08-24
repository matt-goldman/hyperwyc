using System.Net;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Builds synthetic <see cref="HttpResponseMessage"/> instances returned to
/// callers when a request is handled offline by Hyperwyc.
/// </summary>
/// <remarks>
/// <para>
/// The <c>X-Hyperwyc-Status</c> response header is always present on synthetic responses.
/// </para>
/// <para>
/// The handler behaves like a Service Worker: callers receive a normal-looking success
/// response and never need to branch on connectivity. A caller that does want to know reads
/// the status header, or — for a write — the <c>202</c>, which no ordinary success is.
/// </para>
/// </remarks>
internal static class HyperwycResponseFactory
{
    /// <summary>Header name added to every synthetic Hyperwyc response.</summary>
    internal const string StatusHeader = "X-Hyperwyc-Status";

    /// <summary>
    /// Header carrying the correlation id of a queued write, so a caller who did not supply
    /// one through <see cref="HyperwycRequestOptions.CorrelationId"/> still learns the value
    /// Hyperwyc will report the outcome under.
    /// </summary>
    /// <remarks>
    /// Always present on the queued response, whether the value came from the caller or was
    /// generated — one rule rather than "only when we minted it". It appears on a response
    /// Hyperwyc synthesises, so it imposes nothing on anybody's API.
    /// </remarks>
    internal const string CorrelationHeader = "X-Hyperwyc-Correlation-Id";

    /// <summary>
    /// Body given to every synthetic response: the JSON <c>null</c> literal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not an empty body. An empty body is not "no data" to a JSON
    /// deserialiser, it is <em>not JSON at all</em> — <c>GetFromJsonAsync&lt;T&gt;</c>
    /// throws <c>JsonException</c> on it, for a single object every bit as much as for
    /// a collection. Emitting the four characters <c>null</c> instead means the same
    /// call returns <see langword="null"/>, which is a case application code has to
    /// handle anyway.
    /// </para>
    /// <para>
    /// A caller expecting a collection still receives <see langword="null"/> rather
    /// than an empty one, so <c>.Count</c> would fault. Returning <c>[]</c> requires
    /// knowing the route returns a collection, which is per-route knowledge Hyperwyc
    /// does not have — see issue 26.
    /// </para>
    /// <para>
    /// <b>No <c>Content-Type</c> is set.</b> The four characters happen to be valid JSON,
    /// but Hyperwyc does not know what the route serves — it may be SOAP, XML, protobuf
    /// or anything else — and asserting a media type on behalf of an API it knows nothing
    /// about would be a claim it is not entitled to make. It is not needed either:
    /// <c>GetFromJsonAsync&lt;T&gt;</c> does not inspect the content type.
    /// </para>
    /// <para>
    /// For a consumer not using JSON this is no worse than the empty body it replaces —
    /// an XML parser rejects both — and for everyone using the JSON extensions it turns
    /// an exception into a <see langword="null"/>.
    /// </para>
    /// </remarks>
    private const string NullBody = "null";

    private static StringContent JsonNull()
    {
        var content = new StringContent(NullBody, System.Text.Encoding.UTF8);
        content.Headers.ContentType = null;
        return content;
    }

    /// <summary>
    /// Returns a synthetic response indicating that the write request was
    /// persisted to the outbox and will be flushed when connectivity is
    /// restored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>202 Accepted</c> rather than <c>200 OK</c>: the request has been accepted
    /// for later processing but has not yet been performed against the origin server. That
    /// distinction is also how a caller tells a queued write from a delivered one, without
    /// needing to read a header.
    /// </para>
    /// <para>
    /// The body is <c>null</c> rather than empty for the same reason as the read paths:
    /// a caller reading back the created resource — <c>ReadFromJsonAsync&lt;T&gt;</c> on
    /// the response to a POST — gets <see langword="null"/> instead of an exception.
    /// There is no created resource yet, which <c>null</c> states accurately.
    /// </para>
    /// </remarks>
    /// <param name="correlationId">
    /// The value the outcome of this write will be reported under, returned as
    /// <c>X-Hyperwyc-Correlation-Id</c>.
    /// </param>
    internal static HttpResponseMessage Queued(string correlationId)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonNull() };
        response.Headers.TryAddWithoutValidation(StatusHeader, "Queued");
        response.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);
        return response;
    }

    /// <summary>
    /// Returns a synthetic response indicating that the read request could
    /// not be served because the device is offline and no cached response is
    /// available.
    /// </summary>
    internal static HttpResponseMessage Offline()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonNull() };
        response.Headers.TryAddWithoutValidation(StatusHeader, "Offline");
        return response;
    }

    /// <summary>
    /// Returns a synthetic response for a <see cref="CacheStrategy.CacheOnly"/> read
    /// that found nothing cached.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Offline"/> because the device may well be online —
    /// the request was not sent because the route opted out of the network, not
    /// because the network was unavailable. Reporting <c>Offline</c> here would
    /// misdescribe the situation to any caller inspecting the header.
    /// </remarks>
    internal static HttpResponseMessage CacheMiss()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonNull() };
        response.Headers.TryAddWithoutValidation(StatusHeader, "CacheMiss");
        return response;
    }
}
