namespace hyperwyc.Models;

/// <summary>
/// Controls the HTTP status code returned to callers when hyperwyc handles a
/// request offline (queued write or cache miss).
/// </summary>
/// <remarks>
/// Inspired by how Service Workers in PWAs respond to intercepted fetches —
/// the caller never sees a connectivity error; it gets a normal-looking
/// <see cref="System.Net.Http.HttpResponseMessage"/>.  The
/// <c>X-hyperwyc-Status</c> header always indicates the true state
/// (<c>Queued</c> / <c>Offline</c>) regardless of which policy is active.
/// </remarks>
public enum OfflineResponsePolicy
{
    /// <summary>
    /// Return <c>200 OK</c> for queued writes and offline cache misses.
    /// App code does not need to branch on connectivity status; inspect the
    /// <c>X-hyperwyc-Status</c> header if needed. This is the default.
    /// </summary>
    Transparent,

    /// <summary>
    /// Return <c>503 Service Unavailable</c> so callers can detect the
    /// offline / queued state via HTTP status code.
    /// </summary>
    Signal,
}
