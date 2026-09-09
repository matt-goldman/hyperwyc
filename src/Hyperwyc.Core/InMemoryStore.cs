using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// An in-process, non-persistent <see cref="IHyperwycStore"/> backed by a
/// thread-safe in-memory dictionary. Suitable for testing and for scenarios
/// where durability is not required.
/// </summary>
/// <remarks>
/// All state is lost when the process exits. For durable persistence use
/// <c>Hyperwyc.Cabinet</c> instead.
/// </remarks>
public sealed class InMemoryStore : IHyperwycStore
{
    private readonly Dictionary<string, Envelope> _store = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <inheritdoc/>
    public async Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _store.Values
                .FirstOrDefault(e => e.Url == url && e.Response is not null);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. _store.Values
                .Where(e => !e.IsSynced)
                .OrderBy(e => e.CreatedUtc)];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(Envelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _store[envelope.Id] = envelope;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RemoveDeliveredAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Removed rather than flagged. Hyperwyc holds outstanding work and nothing else, so
            // a delivered write leaves with its request body and headers rather than sitting in
            // the store carrying them. See ADR 0010.
            _store.Remove(id);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(urlPrefix);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Removes the record rather than nulling its Response. A husk with no response is
            // reachable by nothing — GetCachedResponseAsync skips it for the null, and the outbox
            // skips it because a cache envelope is IsSynced — so it would sit there until that
            // exact URL happened to be fetched again. Prefix invalidation means the entries least
            // likely to be refetched are exactly the ones it leaves behind. See issue #68.
            //
            // The filter on Response is what makes deleting safe: it selects entries that have a
            // cached response to invalidate, which is what this method is for, and can therefore
            // never match a queued write. Matching on the URL alone also swept up queued writes
            // under the same prefix and wrote each one back unchanged.
            var stale = _store.Values
                .Where(e => e.Response is not null && e.Url.StartsWith(urlPrefix, StringComparison.Ordinal))
                .Select(e => e.Id)
                .ToList();

            foreach (var id in stale)
                _store.Remove(id);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _store.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }
}
