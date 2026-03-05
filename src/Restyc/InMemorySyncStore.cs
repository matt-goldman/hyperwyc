using Restyc.Interfaces;
using Restyc.Models;

namespace Restyc;

/// <summary>
/// An in-process, non-persistent <see cref="ISyncStore"/> backed by a
/// thread-safe in-memory dictionary. Suitable for testing and for scenarios
/// where durability is not required.
/// </summary>
/// <remarks>
/// All state is lost when the process exits. For durable persistence use
/// <c>Restyc.Cabinet</c> instead.
/// </remarks>
public sealed class InMemorySyncStore : ISyncStore
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
                .FirstOrDefault(e => e.Url == url && e.Response is not null && !e.IsDeadLettered);
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
            return _store.Values
                .Where(e => !e.IsSynced && !e.IsDeadLettered)
                .OrderBy(e => e.CreatedUtc)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Envelope>> GetDueForRetryAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _store.Values
                .Where(e => !e.IsSynced && !e.IsDeadLettered && e.NextRetryUtc <= now)
                .OrderBy(e => e.CreatedUtc)
                .ToList();
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
    public async Task MarkSyncedAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store.TryGetValue(id, out var envelope))
                envelope.IsSynced = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task MoveToDeadLetterAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_store.TryGetValue(id, out var envelope))
                envelope.IsDeadLettered = true;
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
            foreach (var envelope in _store.Values.Where(e => e.Url.StartsWith(urlPrefix, StringComparison.Ordinal)))
                envelope.Response = null;
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
