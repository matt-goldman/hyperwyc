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
    internal static HttpResponseMessage Queued(OfflineResponsePolicy policy)
    {
        var statusCode = policy == OfflineResponsePolicy.Transparent
            ? HttpStatusCode.OK
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
}
