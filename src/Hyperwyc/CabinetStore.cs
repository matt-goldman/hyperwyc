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
/// Uses Cabinet's <see cref="RecordSet{T}"/> with an in-memory cache for fast
/// LINQ queries and AES-256-GCM encryption at rest via
/// <see cref="AesGcmEncryptionProvider"/>.
/// </remarks>
public sealed class CabinetStore : IHyperwycStore
{
    private readonly RecordSet<Envelope> _records;
    private readonly string _root;

    // Every operation is serialised. Cabinet's FileOfflineStore saves by writing
    // "Envelope.dat.tmp" and then File.Move-ing it over "Envelope.dat", so two saves in
    // flight at once race: the first Move consumes the temp file and the second throws
    // FileNotFoundException, losing the write. Serialising also makes the read-modify-write
    // sequences below atomic, which they were not — two concurrent callers could each read
    // the same envelope and the second write would silently discard the first.
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
        _records = new RecordSet<Envelope>(store, new RecordSetOptions<Envelope>
        {
            IdSelector = e => e.Id,
        });
    }

    // -------------------------------------------------------------------------
    // IHyperwycStore
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<Envelope?> GetCachedResponseAsync(string url, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await _records.GetAllAsync(ct).ConfigureAwait(false);
            var match = all.FirstOrDefault(e => e.Url == url && e.Response is not null && !e.IsDeadLettered);

            // Hydrated after the filter, never before: loading the record set decrypts no bodies
            // at all, and this reads exactly the one body that is about to be served.
            return match is null ? null : await HydrateAsync(match, ct).ConfigureAwait(false);
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
            var all = await _records.GetAllAsync(ct).ConfigureAwait(false);
            var pending = all
                .Where(e => !e.IsSynced && !e.IsDeadLettered)
                .OrderBy(e => e.CreatedUtc)
                .ToList();

            // Bodies are read for the outbox and not for the cache, which is the asymmetry worth
            // having: the outbox is bounded by what is queued and every envelope in it is about to
            // be replayed, whereas the cache is unbounded and one entry of it gets served.
            var hydrated = new List<Envelope>(pending.Count);
            foreach (var envelope in pending)
                hydrated.Add(await HydrateAsync(envelope, ct).ConfigureAwait(false));

            return hydrated;
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
            var existing = await _records.GetByIdAsync(envelope.Id, ct).ConfigureAwait(false);

            // Blobs first, then the record that references them. An interruption between the two
            // leaves bytes nothing points at, which CompactAttachmentsAsync reclaims; the reverse
            // order leaves a queued write whose body is missing, which would replay wrong. The
            // record write is the commit point. See issue #70.
            await WriteBodiesAsync(envelope, sweepStale: existing is not null, ct).ConfigureAwait(false);

            // What is stored carries no bodies. This is the whole point: RecordSet rewrites the
            // entire set on every single-record change, so anything left inline is re-serialised,
            // re-encrypted and rewritten on every subsequent write — at a 33% base64 premium.
            var stored = WithBodies(envelope, null, null, null);

            if (existing is null)
                await _records.AddAsync(stored, ct).ConfigureAwait(false);
            else
                await _records.UpdateAsync(envelope.Id, stored, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task MarkDeliveredAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var envelope = await _records.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (envelope is null) return;

            envelope.IsSynced = true;
            await _records.UpdateAsync(id, envelope, ct).ConfigureAwait(false);
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
            var envelope = await _records.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (envelope is null) return;

            envelope.IsDeadLettered = true;
            await _records.UpdateAsync(id, envelope, ct).ConfigureAwait(false);
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
            var all = await _records.GetAllAsync(ct).ConfigureAwait(false);

            // See InMemoryStore for why this removes rather than nulls, and why the filter on
            // Response is what makes removing safe. Issue #68.
            //
            // RemoveAsync also deletes the record's attachments, which is where the response body
            // now lives — so the bytes go with the entry rather than orphaning. That is the whole
            // reason this change comes before issue #70 rather than after it.
            var stale = all
                .Where(e => e.Response is not null && e.Url.StartsWith(urlPrefix, StringComparison.Ordinal))
                .Select(e => e.Id)
                .ToList();

            foreach (var id in stale)
                await _records.RemoveAsync(id, ct).ConfigureAwait(false);
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

            // Attachments are nested one directory per record — attachments/{hash(id)}/ — so a
            // top-level file sweep misses every blob in the store. Now that bodies live there,
            // that would have left the reset holding exactly the bytes worth clearing. See #70.
            var attachments = Path.Combine(_root, "attachments");
            if (Directory.Exists(attachments))
                Directory.Delete(attachments, recursive: true);

            Directory.CreateDirectory(attachments);

            // The RecordSet holds its own in-memory copy, which the file deletion knows nothing
            // about. Refreshing drops it so the next read loads from an empty directory.
            await _records.RefreshAsync(ct).ConfigureAwait(false);
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

    // Fixed names rather than anything derived: an envelope has at most one of each, and the
    // names have to survive a round trip through Cabinet's attachment-name hashing.
    private const string RequestBodyName  = "request";
    private const string ResponseBodyName = "response";
    private const string OutcomeBodyName  = "outcome";

    // Cabinet records a content type but nothing here reads it back — the media type a consumer
    // sees comes from the captured headers, which is the only source that survived issue #25.
    private const string BodyContentType = "application/octet-stream";

    private async Task WriteBodiesAsync(Envelope envelope, bool sweepStale, CancellationToken ct)
    {
        // One manifest read covers all three slots, and only on the update path. A record being
        // added has nothing to sweep; an orphan under a reused id is CompactAttachmentsAsync's
        // job, which is what that method exists for.
        var existing = sweepStale
            ? await _records.ListAttachmentsAsync(envelope.Id, ct).ConfigureAwait(false)
            : [];

        await WriteBodyAsync(envelope.Id, RequestBodyName,  envelope.RequestBody,      existing, ct).ConfigureAwait(false);
        await WriteBodyAsync(envelope.Id, ResponseBodyName, envelope.Response?.Body,   existing, ct).ConfigureAwait(false);
        await WriteBodyAsync(envelope.Id, OutcomeBodyName,  envelope.LastOutcome?.Body, existing, ct).ConfigureAwait(false);
    }

    private async Task WriteBodyAsync(
        string id,
        string name,
        byte[]? body,
        IReadOnlyList<AttachmentInfo> existing,
        CancellationToken ct)
    {
        if (body is not null)
        {
            // Replaces an attachment of the same name, so a refetched URL overwrites rather than
            // accumulating — the same property the deterministic cache id gives the record.
            await _records.AddAttachmentAsync(id, new FileAttachment(name, BodyContentType, body), ct)
                .ConfigureAwait(false);
            return;
        }

        // A body that has gone from present to absent takes its blob with it, or the bytes outlive
        // the record that explained them and nothing will ever look for them again.
        if (existing.Any(a => a.Name == name))
            await _records.RemoveAttachmentAsync(id, name, ct).ConfigureAwait(false);
    }

    private async Task<Envelope> HydrateAsync(Envelope stored, CancellationToken ct)
    {
        var request  = await ReadBodyAsync(stored.Id, RequestBodyName, ct).ConfigureAwait(false);

        // Skipped entirely when the enclosing object is absent, so a cache entry costs no probe
        // for an outcome body it cannot have.
        var response = stored.Response is null
            ? null
            : await ReadBodyAsync(stored.Id, ResponseBodyName, ct).ConfigureAwait(false);

        var outcome = stored.LastOutcome is null
            ? null
            : await ReadBodyAsync(stored.Id, OutcomeBodyName, ct).ConfigureAwait(false);

        return WithBodies(stored, request, response, outcome);
    }

    private async Task<byte[]?> ReadBodyAsync(string id, string name, CancellationToken ct)
    {
        // Null when there is no such attachment, which is how "no body" survives the round trip
        // as distinct from an empty one. Cabinet probes the file before reading, so an absent
        // body costs an existence check rather than a decrypt.
        var stream = await _records.OpenAttachmentAsync(id, name, ct).ConfigureAwait(false);
        if (stream is null) return null;

        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>
    /// A copy of <paramref name="source"/> carrying the supplied bodies in place of its own.
    /// </summary>
    /// <remarks>
    /// A copy rather than a mutation, and that is load-bearing in both directions. `RecordSet`
    /// hands out the instances it holds in memory and writes those same instances back on the
    /// next `SaveAllAsync`, so hydrating one in place would put the bodies straight back into the
    /// document this change exists to keep them out of.
    /// </remarks>
    private static Envelope WithBodies(Envelope source, byte[]? request, byte[]? response, byte[]? outcome) =>
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
            IsSynced        = source.IsSynced,
            IsDeadLettered  = source.IsDeadLettered,
            Response        = source.Response is null ? null : new CachedResponse
            {
                StatusCode  = source.Response.StatusCode,
                Headers     = source.Response.Headers,
                Body        = response,
                CachedAt    = source.Response.CachedAt,
            },
            LastOutcome     = source.LastOutcome is null ? null : source.LastOutcome with { Body = outcome },
        };
}
