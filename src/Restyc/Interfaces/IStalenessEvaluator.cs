using Restyc.Models;

namespace Restyc.Interfaces;

/// <summary>
/// Determines whether a cached response is still considered fresh and may be
/// returned to the caller without a network round-trip.
/// </summary>
public interface IStalenessEvaluator
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="cachedEnvelope"/> is
    /// stale as of <paramref name="now"/> and should not be served from cache.
    /// </summary>
    bool IsStale(Envelope cachedEnvelope, DateTimeOffset now);
}
