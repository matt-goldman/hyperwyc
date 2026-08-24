using Hyperwyc.Models;

namespace Hyperwyc.Interfaces;

/// <summary>
/// Provides per-request caching rules. Implement this interface to customise
/// which endpoints are cached, which use network-only semantics, and how
/// aggressively Hyperwyc retries failed writes.
/// </summary>
public interface ISyncPolicy
{
    /// <summary>
    /// Returns the <see cref="CacheStrategy"/> that should be applied when
    /// handling <paramref name="request"/>.
    /// </summary>
    CacheStrategy GetStrategy(HttpRequestMessage request);

    /// <summary>
    /// Returns <see langword="true"/> if a successful write for
    /// <paramref name="request"/> should invalidate cached responses whose URL
    /// shares the same path prefix.
    /// </summary>
    bool ShouldInvalidateCacheOnWrite(HttpRequestMessage request);
}
