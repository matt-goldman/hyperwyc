using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc;

/// <summary>
/// An in-process, non-persistent <see cref="IHyperwycStore"/> backed by
/// thread-safe in-memory dictionaries. Suitable for testing and for scenarios
/// where durability is not required.
/// </summary>
/// <remarks>
/// <para>
/// All state is lost when the process exits. For durable persistence use
/// <c>Hyperwyc.Cabinet</c> instead.
/// </para>
/// <para>
/// It does not implement <see cref="IHyperwycStore.TryQuarantineAsync"/>, taking the default
/// that declines. There is nowhere to set anything aside, and nothing to set aside anyway — a
/// dictionary does not become unreadable. See issue 62.
/// </para>
/// </remarks>
public sealed class InMemoryStore : IHyperwycStore
{
    // Two dictionaries rather than one keyed by a discriminator: the two kinds have different
    // keys — a URL and a generated id — and no query has any business seeing both. Issue 55.
    private readonly Dictionary<string, CachedResponse> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueuedWrite> _outbox = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    // -------------------------------------------------------------------------
    // Cache
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<CachedResponse?> GetCachedResponseAsync(string url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _cache.GetValueOrDefault(url);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task PutCachedResponseAsync(CachedResponse response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Keyed on the URL, so a refetch replaces rather than accumulating.
            _cache[response.Url] = response;
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
            // Removes the record rather than emptying it. An entry with no body is reachable by
            // nothing, so it would sit there until that exact URL happened to be fetched again —
            // and prefix invalidation means the entries least likely to be refetched are exactly
            // the ones it leaves behind. See issue 68.
            //
            // Queued writes are in the other dictionary, so a write under the same prefix is out
            // of reach by construction. This used to be a filter on Response being non-null,
            // which was a kind check written as a field check.
            var stale = _cache.Keys
                .Where(url => url.StartsWith(urlPrefix, StringComparison.Ordinal))
                .ToList();

            foreach (var url in stale)
                _cache.Remove(url);
        }
        finally
        {
            _lock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Outbox
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<IReadOnlyList<QueuedWrite>> GetPendingOutboxAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. _outbox.Values.OrderBy(w => w.CreatedUtc)];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task UpsertQueuedWriteAsync(QueuedWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _outbox[write.Id] = write;
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
            _outbox.Remove(id);
        }
        finally
        {
            _lock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Both
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cache.Clear();
            _outbox.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }
}
