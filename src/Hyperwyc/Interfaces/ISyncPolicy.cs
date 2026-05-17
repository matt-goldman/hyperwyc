using hyperwyc.Models;

namespace hyperwyc.Interfaces;

/// <summary>
/// Provides per-request caching rules. Implement this interface to customise
/// which endpoints are cached, which use network-only semantics, and how
/// aggressively hyperwyc retries failed writes.
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

    /// <summary>
    /// Returns the retry configuration that governs how many times a failed
    /// outbox entry for <paramref name="request"/> is retried before being
    /// moved to the dead-letter queue.
    /// </summary>
    RetryOptions GetRetryOptions(HttpRequestMessage request);
}
