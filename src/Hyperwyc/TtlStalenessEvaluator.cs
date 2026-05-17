using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// Default <see cref="IStalenessEvaluator"/> that considers a cached response
/// stale once <see cref="CachedResponse.CachedAt"/> plus the configured TTL
/// has elapsed.
/// </summary>
public sealed class TtlStalenessEvaluator : IStalenessEvaluator
{
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Initialises the evaluator with the TTL from <paramref name="options"/>.
    /// </summary>
    public TtlStalenessEvaluator(HyperwycOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ttl = options.DefaultCacheTtl;
    }

    /// <summary>
    /// Initialises the evaluator with an explicit <paramref name="ttl"/>.
    /// </summary>
    public TtlStalenessEvaluator(TimeSpan ttl) => _ttl = ttl;

    /// <inheritdoc/>
    public bool IsStale(Envelope cachedEnvelope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cachedEnvelope);

        if (cachedEnvelope.Response is null)
            return true;

        return now >= cachedEnvelope.Response.CachedAt + _ttl;
    }
}
