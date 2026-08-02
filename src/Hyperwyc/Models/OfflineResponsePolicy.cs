namespace Hyperwyc.Models;

/// <summary>
/// Controls the HTTP status code returned to callers when Hyperwyc handles a
/// request offline (queued write or cache miss).
/// </summary>
/// <remarks>
/// Inspired by how Service Workers in PWAs respond to intercepted fetches —
/// the caller never sees a connectivity error; it gets a normal-looking
/// <see cref="System.Net.Http.HttpResponseMessage"/>.  The
/// <c>X-Hyperwyc-Status</c> header always indicates the true state
/// (<c>Queued</c> / <c>Offline</c>) regardless of which policy is active.
/// </remarks>
public enum OfflineResponsePolicy
{
    /// <summary>
    /// Return a success status for queued writes and offline cache misses —
    /// <c>202 Accepted</c> for a queued write, <c>200 OK</c> for an offline read
    /// with no cached response. App code does not need to branch on connectivity
    /// status; inspect the <c>X-Hyperwyc-Status</c> header if needed.
    /// This is the default.
    /// </summary>
    Transparent,

    /// <summary>
    /// Return <c>503 Service Unavailable</c> so callers can detect the
    /// offline / queued state via HTTP status code.
    /// </summary>
    Signal,
}
