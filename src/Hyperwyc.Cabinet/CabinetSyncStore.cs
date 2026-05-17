using Cabinet.Core;
using Cabinet.Security;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc.Cabinet;

/// <summary>
/// A durable <see cref="ISyncStore"/> backed by Cabinet, suitable for production
/// use in .NET MAUI (iOS, Android, macOS, Windows) and other .NET applications.
/// </summary>
/// <remarks>
/// Uses Cabinet's <see cref="RecordSet{T}"/> with an in-memory cache for fast
/// LINQ queries and AES-256-GCM encryption at rest via
/// <see cref="AesGcmEncryptionProvider"/>.
/// </remarks>
public sealed class CabinetSyncStore : ISyncStore
{
    private readonly RecordSet<Envelope> _records;

    /// <summary>
    /// Initialises a new <see cref="CabinetSyncStore"/> with a per-path key
    /// derived from <paramref name="dbDirectory"/> via SHA-256.
    /// </summary>
    /// <remarks>
    /// The derived key is deterministic for a given path but is not
    /// cryptographically strong. For production use, supply an explicit key
    /// via the two-parameter overload.
    /// </remarks>
    /// <param name="dbDirectory">
    /// Directory path where Cabinet will write its encrypted files.
    /// Created automatically if it does not exist.
    /// </param>
    public CabinetSyncStore(string dbDirectory)
        : this(dbDirectory, DeriveKey(dbDirectory)) { }

    /// <summary>
    /// Initialises a new <see cref="CabinetSyncStore"/> with an explicit 32-byte
    /// AES-256 encryption key.
    /// </summary>
    /// <param name="dbDirectory">
    /// Directory path where Cabinet will write its encrypted files.
    /// Created automatically if it does not exist.
    /// </param>
    /// <param name="encryptionKey">
    /// A 32-byte (256-bit) encryption key. Keep this secret — losing it
    /// means losing access to all stored data.
    /// </param>
    public CabinetSyncStore(string dbDirectory, byte[] encryptionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbDirectory);
        ArgumentNullException.ThrowIfNull(encryptionKey);

        var crypto = new AesGcmEncryptionProvider(encryptionKey);
        var store = new FileOfflineStore(dbDirectory, crypto, null);
        _records = new RecordSet<Envelope>(store, new RecordSetOptions<Envelope>
        {
            IdSelector = e => e.Id,
        });
    }

    // -------------------------------------------------------------------------
    // ISyncStore
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default)
    {
        var all = await _records.GetAllAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(e => e.Url == url && e.Response is not null && !e.IsDeadLettered);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Envelope>> GetPendingOutboxAsync(CancellationToken ct = default)
    {
        var all = await _records.GetAllAsync(ct).ConfigureAwait(false);
        return [.. all
            .Where(e => !e.IsSynced && !e.IsDeadLettered)
            .OrderBy(e => e.CreatedUtc)];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Envelope>> GetDueForRetryAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var all = await _records.GetAllAsync(ct).ConfigureAwait(false);
        return [.. all
            .Where(e => !e.IsSynced && !e.IsDeadLettered && e.NextRetryUtc <= now)
            .OrderBy(e => e.CreatedUtc)];
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(Envelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var existing = await _records.GetByIdAsync(envelope.Id, ct).ConfigureAwait(false);
        if (existing is null)
            await _records.AddAsync(envelope, ct).ConfigureAwait(false);
        else
            await _records.UpdateAsync(envelope.Id, envelope, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task MarkSyncedAsync(string id, CancellationToken ct = default)
    {
        var envelope = await _records.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (envelope is null) return;

        envelope.IsSynced = true;
        await _records.UpdateAsync(id, envelope, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task MoveToDeadLetterAsync(string id, CancellationToken ct = default)
    {
        var envelope = await _records.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (envelope is null) return;

        envelope.IsDeadLettered = true;
        await _records.UpdateAsync(id, envelope, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task InvalidateCacheForPrefixAsync(string urlPrefix, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(urlPrefix);

        var all = await _records.GetAllAsync(ct).ConfigureAwait(false);
        foreach (var envelope in all.Where(e => e.Url.StartsWith(urlPrefix, StringComparison.Ordinal)).ToList())
        {
            envelope.Response = null;
            await _records.UpdateAsync(envelope.Id, envelope, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        var all = await _records.GetAllAsync(ct).ConfigureAwait(false);
        foreach (var envelope in all.ToList())
            await _records.RemoveAsync(envelope.Id, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static byte[] DeriveKey(string path) =>
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path));
}
