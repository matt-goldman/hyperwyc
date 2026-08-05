using System.Net;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Builds synthetic <see cref="HttpResponseMessage"/> instances returned to
/// callers when a request is handled offline by Hyperwyc.
/// </summary>
/// <remarks>
/// <para>
/// The <c>X-Hyperwyc-Status</c> response header is always present on synthetic
/// responses regardless of the <see cref="OfflineResponsePolicy"/>.
/// </para>
/// <para>
/// With <see cref="OfflineResponsePolicy.Transparent"/> (the default) the
/// handler behaves like a Service Worker — callers receive <c>200 OK</c> and
/// never need to branch on connectivity.  With
/// <see cref="OfflineResponsePolicy.Signal"/> the handler returns <c>503</c>
/// so callers can detect the offline state through standard HTTP semantics.
/// </para>
/// </remarks>
internal static class HyperwycResponseFactory
{
    /// <summary>Header name added to every synthetic Hyperwyc response.</summary>
    internal const string StatusHeader = "X-Hyperwyc-Status";

    /// <summary>
    /// Returns a synthetic response indicating that the write request was
    /// persisted to the outbox and will be flushed when connectivity is
    /// restored.
    /// </summary>
    /// <remarks>
    /// Under <see cref="OfflineResponsePolicy.Transparent"/> this returns
    /// <c>202 Accepted</c> rather than <c>200 OK</c>: the request has been
    /// accepted for later processing but has not yet been performed against
    /// the origin server.
    /// </remarks>
    internal static HttpResponseMessage Queued(OfflineResponsePolicy policy)
    {
        var statusCode = policy == OfflineResponsePolicy.Transparent
            ? HttpStatusCode.Accepted
            : HttpStatusCode.ServiceUnavailable;
        var response = new HttpResponseMessage(statusCode);
        response.Headers.TryAddWithoutValidation(StatusHeader, "Queued");
        return response;
    }

    /// <summary>
    /// Returns a synthetic response indicating that the read request could
    /// not be served because the device is offline and no cached response is
    /// available.
    /// </summary>
    internal static HttpResponseMessage Offline(OfflineResponsePolicy policy)
    {
        var statusCode = policy == OfflineResponsePolicy.Transparent
            ? HttpStatusCode.OK
            : HttpStatusCode.ServiceUnavailable;
        var response = new HttpResponseMessage(statusCode);
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
    internal static HttpResponseMessage CacheMiss(OfflineResponsePolicy policy)
    {
        var statusCode = policy == OfflineResponsePolicy.Transparent
            ? HttpStatusCode.OK
            : HttpStatusCode.ServiceUnavailable;
        var response = new HttpResponseMessage(statusCode);
        response.Headers.TryAddWithoutValidation(StatusHeader, "CacheMiss");
        return response;
    }
}
