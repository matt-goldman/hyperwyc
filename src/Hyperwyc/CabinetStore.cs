using System.Text.Json;
using Cabinet.Core;
using Cabinet.Security;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc.Cabinet;

/// <summary>
/// A durable <see cref="IHyperwycStore"/> backed by Cabinet, suitable for production
/// use in .NET MAUI (iOS, Android, macOS, Windows) and other .NET applications.
/// </summary>
/// <remarks>
/// <para>
/// Uses Cabinet's <see cref="RecordSet{T}"/> with an in-memory cache for fast
/// LINQ queries and AES-256-GCM encryption at rest via
/// <see cref="AesGcmEncryptionProvider"/>.
/// </para>
/// <para>
/// <b>One record set per kind.</b> Cached responses and queued writes are different types with
/// different keys, so they are different sets — separate documents, separate id spaces, and no
/// query that can reach the wrong one. That is what the deterministic <c>cache:</c> id prefix
/// used to buy, in a store where both kinds shared a set. See issue 55.
/// </para>
/// </remarks>
public sealed class CabinetStore : IHyperwycStore
{
    private readonly RecordSet<CachedResponse> _cache;
    private readonly RecordSet<QueuedWrite> _outbox;
    private readonly string _root;

    // Every operation is serialised. Cabinet's FileOfflineStore saves by writing
    // "<set>.dat.tmp" and then File.Move-ing it over "<set>.dat", so two saves in
    // flight at once race: the first Move consumes the temp file and the second throws
    // FileNotFoundException, losing the write. Serialising also makes the read-modify-write
    // sequences below atomic, which they were not — two concurrent callers could each read
    // the same record and the second write would silently discard the first.
    //
    // One lock across both sets rather than one each: they share the underlying
    // FileOfflineStore, and a cache write and an outbox write are as capable of racing each
    // other inside it as two writes to the same set.
    //
    // Concurrency is not exotic here. HyperwycHandler is transient and runs on whatever
    // thread the caller used, so two overlapping HTTP requests reach the store at once, and
    // an processor flush runs on a background task alongside all of it.
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initialises a new <see cref="CabinetStore"/> with a per-path key
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
    public CabinetStore(string dbDirectory)
        : this(dbDirectory, DeriveKey(dbDirectory)) { }

    /// <summary>
    /// Initialises a new <see cref="CabinetStore"/> from
    /// <paramref name="options"/>. This is the constructor the DI container uses when
    /// the store is registered by type.
    /// </summary>
    /// <remarks>
    /// Configuration arrives through an options object rather than as positional
    /// parameters precisely so that the container can construct this type. A
    /// constructor taking a bare <see cref="string"/> cannot be resolved from DI.
    /// </remarks>
    /// <param name="options">Store location and encryption settings.</param>
    public CabinetStore(CabinetStoreOptions options)
        : this(DirectoryFrom(options), KeyFrom(options)) { }

    // Arguments are evaluated left to right, so the null check in DirectoryFrom runs
    // before KeyFrom dereferences options.
    private static string DirectoryFrom(CabinetStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.DirectoryPath;
    }

    private static byte[] KeyFrom(CabinetStoreOptions options) =>
        options.EncryptionKey ?? DeriveKey(options.DirectoryPath);

    /// <summary>
    /// Initialises a new <see cref="CabinetStore"/> with an explicit 32-byte
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
    public CabinetStore(string dbDirectory, byte[] encryptionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbDirectory);
        ArgumentNullException.ThrowIfNull(encryptionKey);

        _root = dbDirectory;

        var crypto = new AesGcmEncryptionProvider(encryptionKey);

        // The JSON options are what make the store AOT- and trim-safe. Without them Cabinet
        // falls back to its own reflection-based default, which works in a debug build and
        // fails on a trimmed or AOT-compiled one — iOS release builds have AOT on by default,
        // so that failure lands at store submission rather than in development. See issue #53.
        //
        // Named argument on the indexer because the two constructors differ only in this
        // position: a bare null binds to the overload taking IIndexProvider?, which is how the
        // options came to be missing in the first place.
        var store = new FileOfflineStore(
            dbDirectory,
            crypto,
            new JsonSerializerOptions { TypeInfoResolver = HyperwycJsonContext.Default },
            indexer: null);

        // A cache entry's identity is the URL it caches, which is what makes a refetch replace
        // the previous response rather than sit beside it. Attachments are namespaced by the
        // set, so the two sets cannot collide even on an id they happen to share.
        _cache = new RecordSet<CachedResponse>(store, new RecordSetOptions<CachedResponse>
        {
            IdSelector = c => c.Url,
        });

        _outbox = new RecordSet<QueuedWrite>(store, new RecordSetOptions<QueuedWrite>
        {
            IdSelector = w => w.Id,
        });
    }

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
            var match = await _cache.GetByIdAsync(url, ct).ConfigureAwait(false);

            // Hydrated after the lookup, never before: loading the record set decrypts no bodies
            // at all, and this reads exactly the one body that is about to be served.
            return match is null ? null : await HydrateAsync(match, ct).ConfigureAwait(false);
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
            var existing = await _cache.GetByIdAsync(response.Url, ct).ConfigureAwait(false);

            // Blob first, then the record that references it. An interruption between the two
            // leaves bytes nothing points at, which CompactAttachmentsAsync reclaims; the
            // reverse order leaves an entry whose body is missing, which would serve wrong. The
            // record write is the commit point. See issue #70.
            await WriteBodyAsync(_cache, response.Url, BodyName, response.Body, existing is not null, ct)
                .ConfigureAwait(false);

            // What is stored carries no body. This is the whole point: RecordSet rewrites the
            // entire set on every single-record change, so anything left inline is re-serialised,
            // re-encrypted and rewritten on every subsequent write — at a 33% base64 premium.
            var stored = WithBody(response, null);

            if (existing is null)
                await _cache.AddAsync(stored, ct).ConfigureAwait(false);
            else
                await _cache.UpdateAsync(response.Url, stored, ct).ConfigureAwait(false);
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
            var all = await _cache.GetAllAsync(ct).ConfigureAwait(false);

            // Removes the record rather than emptying it — see InMemoryStore, and issue #68.
            //
            // RemoveAsync also deletes the record's attachments, which is where the response body
            // lives, so the bytes go with the entry rather than orphaning.
            //
            // Queued writes are a different set entirely, so a write under the same prefix is out
            // of reach here by construction rather than by a filter. Issue 55.
            var stale = all
                .Where(c => c.Url.StartsWith(urlPrefix, StringComparison.Ordinal))
                .Select(c => c.Url)
                .ToList();

            foreach (var url in stale)
                await _cache.RemoveAsync(url, ct).ConfigureAwait(false);
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
            var all = await _outbox.GetAllAsync(ct).ConfigureAwait(false);
            var pending = all
                .OrderBy(w => w.CreatedUtc)
                .ToList();

            // Bodies are read for the outbox and not for the cache, which is the asymmetry worth
            // having: the outbox is bounded by what is queued and every write in it is about to
            // be replayed, whereas the cache is unbounded and one entry of it gets served.
            var hydrated = new List<QueuedWrite>(pending.Count);
            foreach (var write in pending)
                hydrated.Add(await HydrateAsync(write, ct).ConfigureAwait(false));

            return hydrated;
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
            var existing = await _outbox.GetByIdAsync(write.Id, ct).ConfigureAwait(false);

            // Blobs first, then the record that references them — the same ordering, and for the
            // same reason, as the cache above: the reverse leaves a queued write whose body is
            // missing, which would replay wrong. See issue #70.
            var sweepStale = existing is not null;
            await WriteBodyAsync(_outbox, write.Id, RequestBodyName, write.RequestBody, sweepStale, ct)
                .ConfigureAwait(false);
            await WriteBodyAsync(_outbox, write.Id, OutcomeBodyName, write.LastOutcome?.Body, sweepStale, ct)
                .ConfigureAwait(false);

            var stored = WithBodies(write, null, null);

            if (existing is null)
                await _outbox.AddAsync(stored, ct).ConfigureAwait(false);
            else
                await _outbox.UpdateAsync(write.Id, stored, ct).ConfigureAwait(false);
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
            // RemoveAsync deletes the record's attachments too, which is where the request body
            // lives (issue #70). That matters more here than anywhere else: the body and the
            // captured headers are the most sensitive bytes in the store, and delivery is the
            // moment they stop being needed. See ADR 0010.
            await _outbox.RemoveAsync(id, ct).ConfigureAwait(false);
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
            // Deletes the files rather than enumerating records and removing them one by one.
            // Enumerating means decrypting and deserialising, which is precisely what a store
            // worth resetting cannot do — the remedy would have been unavailable in the only
            // case that needs it. See issue #49.
            foreach (var directory in new[] { "records", "index" })
            {
                var path = Path.Combine(_root, directory);
                if (!Directory.Exists(path)) continue;

                foreach (var file in Directory.GetFiles(path))
                    File.Delete(file);
            }

            // Attachments are nested one directory per record — attachments/{hash(key)}/ — so a
            // top-level file sweep misses every blob in the store. Now that bodies live there,
            // that would have left the reset holding exactly the bytes worth clearing. See #70.
            var attachments = Path.Combine(_root, "attachments");
            if (Directory.Exists(attachments))
                Directory.Delete(attachments, recursive: true);

            Directory.CreateDirectory(attachments);

            // Each RecordSet holds its own in-memory copy, which the file deletion knows nothing
            // about. Refreshing drops them so the next read loads from an empty directory.
            await _cache.RefreshAsync(ct).ConfigureAwait(false);
            await _outbox.RefreshAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static byte[] DeriveKey(string path) =>
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path));

    // -------------------------------------------------------------------------
    // Bodies (issue #70)
    // -------------------------------------------------------------------------

    // Fixed names rather than anything derived: a record has at most one of each, and the
    // names have to survive a round trip through Cabinet's attachment-name hashing.
    private const string BodyName         = "body";
    private const string RequestBodyName  = "request";
    private const string OutcomeBodyName  = "outcome";

    // Cabinet records a content type but nothing here reads it back — the media type a consumer
    // sees comes from the captured headers, which is the only source that survived issue #25.
    private const string BodyContentType = "application/octet-stream";

    private static async Task WriteBodyAsync<T>(
        RecordSet<T> records,
        string id,
        string name,
        byte[]? body,
        bool sweepStale,
        CancellationToken ct)
        where T : class
    {
        if (body is not null)
        {
            // Replaces an attachment of the same name, so a refetched URL overwrites rather than
            // accumulating — the same property keying the cache on its URL gives the record.
            await records.AddAttachmentAsync(id, new FileAttachment(name, BodyContentType, body), ct)
                .ConfigureAwait(false);
            return;
        }

        // A body that has gone from present to absent takes its blob with it, or the bytes outlive
        // the record that explained them and nothing will ever look for them again. Only worth
        // checking on the update path: a record being added has nothing to sweep, and an orphan
        // under a reused id is CompactAttachmentsAsync's job.
        if (!sweepStale) return;

        var existing = await records.ListAttachmentsAsync(id, ct).ConfigureAwait(false);
        if (existing.Any(a => a.Name == name))
            await records.RemoveAttachmentAsync(id, name, ct).ConfigureAwait(false);
    }

    private async Task<CachedResponse> HydrateAsync(CachedResponse stored, CancellationToken ct) =>
        WithBody(stored, await ReadBodyAsync(_cache, stored.Url, BodyName, ct).ConfigureAwait(false));

    private async Task<QueuedWrite> HydrateAsync(QueuedWrite stored, CancellationToken ct)
    {
        var request = await ReadBodyAsync(_outbox, stored.Id, RequestBodyName, ct).ConfigureAwait(false);

        // Skipped entirely when the enclosing object is absent, so a write that has never been
        // attempted costs no probe for an outcome body it cannot have.
        var outcome = stored.LastOutcome is null
            ? null
            : await ReadBodyAsync(_outbox, stored.Id, OutcomeBodyName, ct).ConfigureAwait(false);

        return WithBodies(stored, request, outcome);
    }

    private static async Task<byte[]?> ReadBodyAsync<T>(
        RecordSet<T> records, string id, string name, CancellationToken ct)
        where T : class
    {
        // Null when there is no such attachment, which is how "no body" survives the round trip
        // as distinct from an empty one. Cabinet probes the file before reading, so an absent
        // body costs an existence check rather than a decrypt.
        var stream = await records.OpenAttachmentAsync(id, name, ct).ConfigureAwait(false);
        if (stream is null) return null;

        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>
    /// A copy of <paramref name="source"/> carrying <paramref name="body"/> in place of its own.
    /// </summary>
    /// <remarks>
    /// A copy rather than a mutation, and that is load-bearing in both directions. <c>RecordSet</c>
    /// hands out the instances it holds in memory and writes those same instances back on the
    /// next <c>SaveAllAsync</c>, so hydrating one in place would put the body straight back into
    /// the document this change exists to keep it out of.
    /// </remarks>
    private static CachedResponse WithBody(CachedResponse source, byte[]? body) =>
        new()
        {
            Url         = source.Url,
            StatusCode  = source.StatusCode,
            Headers     = source.Headers,
            Body        = body,
            CachedAt    = source.CachedAt,
        };

    /// <summary>
    /// A copy of <paramref name="source"/> carrying the supplied bodies in place of its own.
    /// </summary>
    /// <remarks>See <see cref="WithBody"/> for why this is a copy.</remarks>
    private static QueuedWrite WithBodies(QueuedWrite source, byte[]? request, byte[]? outcome) =>
        new()
        {
            Id              = source.Id,
            CorrelationId   = source.CorrelationId,
            Url             = source.Url,
            Method          = source.Method,
            ClientName      = source.ClientName,
            RequestHeaders  = source.RequestHeaders,
            RequestBody     = request,
            CreatedUtc      = source.CreatedUtc,
            LastOutcome     = source.LastOutcome is null ? null : source.LastOutcome with { Body = outcome },
        };
}
