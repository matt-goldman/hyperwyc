using Restyc.Interfaces;
using Restyc.Models;

namespace Restyc;

/// <summary>
/// Factory for common <see cref="ISyncPolicy"/> presets.
/// </summary>
/// <example>
/// <code>
/// services.AddRestyc(options =>
/// {
///     options.DefaultPolicy = SyncPolicy.CacheFirst(TimeSpan.FromDays(1));
/// });
/// </code>
/// </example>
public static class SyncPolicy
{
    private static readonly RetryOptions _defaultRetry =
        new(MaxRetries: 5, InitialDelay: TimeSpan.FromSeconds(2), BackoffMultiplier: 2.0);

    /// <summary>
    /// Returns a cached response when available and fresh; falls back to the
    /// network only when the cache is empty or stale. Successful writes
    /// invalidate the cache for the affected URL prefix.
    /// </summary>
    /// <param name="ttl">
    /// How long a cached response is considered fresh.
    /// Also applied as <see cref="RestycOptions.DefaultCacheTtl"/> when this
    /// policy is set as <see cref="RestycOptions.DefaultPolicy"/>.
    /// </param>
    public static ISyncPolicy CacheFirst(TimeSpan ttl) =>
        new PresetSyncPolicy(CacheStrategy.CacheFirst, shouldInvalidate: true, ttl);

    /// <summary>
    /// Always calls the API first; falls back to the cache only when the
    /// network is unavailable.
    /// </summary>
    public static ISyncPolicy ApiFirst() =>
        new PresetSyncPolicy(CacheStrategy.ApiFirst, shouldInvalidate: true, ttl: null);

    /// <summary>
    /// Only ever returns a cached response. Never makes a network request.
    /// </summary>
    public static ISyncPolicy CacheOnly() =>
        new PresetSyncPolicy(CacheStrategy.CacheOnly, shouldInvalidate: false, ttl: null);

    /// <summary>
    /// Always calls the API. Never reads from or writes to the cache.
    /// </summary>
    public static ISyncPolicy NetworkOnly() =>
        new PresetSyncPolicy(CacheStrategy.NetworkOnly, shouldInvalidate: false, ttl: null);

    // -------------------------------------------------------------------------
    // Internal preset implementation
    // -------------------------------------------------------------------------

    internal sealed class PresetSyncPolicy(
        CacheStrategy strategy,
        bool shouldInvalidate,
        TimeSpan? ttl) : ISyncPolicy
    {
        /// <summary>Optional TTL to propagate to RestycOptions.DefaultCacheTtl.</summary>
        internal TimeSpan? Ttl { get; } = ttl;

        public CacheStrategy GetStrategy(HttpRequestMessage request) => strategy;

        public bool ShouldInvalidateCacheOnWrite(HttpRequestMessage request) => shouldInvalidate;

        public RetryOptions GetRetryOptions(HttpRequestMessage request) => _defaultRetry;
    }
}
