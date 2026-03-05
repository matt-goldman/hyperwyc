using System.Net;

namespace Restyc;

/// <summary>
/// Builds synthetic <see cref="HttpResponseMessage"/> instances returned to
/// callers when a request is handled offline by Restyc.
/// </summary>
/// <remarks>
/// Callers can detect synthetic responses by inspecting the
/// <c>X-Restyc-Status</c> response header.
/// </remarks>
internal static class RestycResponseFactory
{
    /// <summary>Header name added to every synthetic Restyc response.</summary>
    internal const string StatusHeader = "X-Restyc-Status";

    /// <summary>
    /// Returns a <c>503 Service Unavailable</c> response indicating that the
    /// write request was persisted to the outbox and will be flushed when
    /// connectivity is restored.
    /// </summary>
    internal static HttpResponseMessage Queued()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryAddWithoutValidation(StatusHeader, "Queued");
        return response;
    }

    /// <summary>
    /// Returns a <c>503 Service Unavailable</c> response indicating that the
    /// read request could not be served because the device is offline and no
    /// cached response is available.
    /// </summary>
    internal static HttpResponseMessage Offline()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryAddWithoutValidation(StatusHeader, "Offline");
        return response;
    }
}
