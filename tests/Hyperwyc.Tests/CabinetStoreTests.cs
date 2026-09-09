using Hyperwyc.Cabinet;
using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Cabinet.Tests;

/// <summary>
/// Integration tests for <see cref="CabinetStore"/> using a real Cabinet
/// in-process instance backed by a temporary directory.
/// </summary>
public class CabinetStoreTests : IDisposable
{
    // A fixed 32-byte test key (NOT for production use).
    private static readonly byte[] TestKey =
        System.Security.Cryptography.SHA256.HashData("Hyperwyc-test"u8.ToArray());

    private readonly string _tempDir;
    private readonly CabinetStore _store;

    public CabinetStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Hyperwyc-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _store = new CabinetStore(_tempDir, TestKey);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // -------------------------------------------------------------------------
    // PutCachedResponseAsync / GetCachedResponseAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PutCachedResponseAsync_CanBeReadBackByUrl()
    {
        await _store.PutCachedResponseAsync(Cached("https://example.com/api/items"));

        var result = await _store.GetCachedResponseAsync("https://example.com/api/items");

        Assert.NotNull(result);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task PutCachedResponseAsync_SameUrl_ReplacesTheEntry()
    {
        await _store.PutCachedResponseAsync(Cached("https://example.com/api/items"));

        await _store.PutCachedResponseAsync(new CachedResponse
        {
            Url = "https://example.com/api/items",
            StatusCode = 200,
            Body = "updated"u8.ToArray(),
            CachedAt = DateTimeOffset.UtcNow,
        });

        var result = await _store.GetCachedResponseAsync("https://example.com/api/items");
        Assert.Equal("updated", result!.GetBodyAsText());
    }

    [Fact]
    public async Task GetCachedResponseAsync_NoMatch_ReturnsNull()
    {
        var result = await _store.GetCachedResponseAsync("https://example.com/api/missing");
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // GetPendingOutboxAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPendingOutboxAsync_ReturnsQueuedWritesInOrder()
    {
        var first  = new QueuedWrite { Url = "https://example.com/a", Method = "POST" };
        await Task.Delay(5);
        var second = new QueuedWrite { Url = "https://example.com/b", Method = "POST" };
        await Task.Delay(5);
        var third  = new QueuedWrite { Url = "https://example.com/c", Method = "POST" };

        // Insert in reverse order to verify ordering.
        await _store.UpsertQueuedWriteAsync(third);
        await _store.UpsertQueuedWriteAsync(first);
        await _store.UpsertQueuedWriteAsync(second);

        var pending = await _store.GetPendingOutboxAsync();

        Assert.Equal(3, pending.Count);
        Assert.Equal("https://example.com/a", pending[0].Url);
        Assert.Equal("https://example.com/b", pending[1].Url);
        Assert.Equal("https://example.com/c", pending[2].Url);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_ExcludesCacheEntries()
    {
        // A property of the record sets now rather than of a filter: the two kinds are stored
        // separately, so the outbox query cannot see a cached response at all. See issue 55.
        await _store.UpsertQueuedWriteAsync(
            new QueuedWrite { Url = "https://example.com/pending", Method = "POST" });
        await _store.PutCachedResponseAsync(Cached("https://example.com/cached"));

        var result = await _store.GetPendingOutboxAsync();

        Assert.Single(result);
        Assert.Equal("https://example.com/pending", result[0].Url);
    }

    // -------------------------------------------------------------------------
    // RemoveDeliveredAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RemoveDeliveredAsync_RemovesTheWriteFromPendingOutbox()
    {
        var write = new QueuedWrite { Url = "https://example.com/api/orders", Method = "POST" };
        await _store.UpsertQueuedWriteAsync(write);

        await _store.RemoveDeliveredAsync(write.Id);

        Assert.Empty(await _store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task RemoveDeliveredAsync_DoesNotLeaveTheRecordBehind()
    {
        // Reopening the store is the evidence: nothing is left to read back. See ADR 0010 —
        // the request body and its headers go with the record, which is most of the point.
        var write = new QueuedWrite
        {
            Url             = "https://example.com/api/orders",
            Method          = "POST",
            RequestHeaders  = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
            RequestBody     = "{}"u8.ToArray(),
        };
        await _store.UpsertQueuedWriteAsync(write);

        await _store.RemoveDeliveredAsync(write.Id);

        var reopened = new CabinetStore(_tempDir, TestKey);
        Assert.Empty(await reopened.GetPendingOutboxAsync());
        Assert.Empty(AttachmentBlobs());
    }

    // -------------------------------------------------------------------------
    // InvalidateCacheForPrefixAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_RemovesMatchingEntries()
    {
        await _store.PutCachedResponseAsync(Cached("https://example.com/api/notes"));
        await _store.PutCachedResponseAsync(Cached("https://example.com/api/notes/42"));
        await _store.PutCachedResponseAsync(Cached("https://example.com/api/other"));

        await _store.InvalidateCacheForPrefixAsync("https://example.com/api/notes");

        Assert.Null(await _store.GetCachedResponseAsync("https://example.com/api/notes"));
        Assert.Null(await _store.GetCachedResponseAsync("https://example.com/api/notes/42"));
        // Unrelated URL should be unaffected.
        Assert.NotNull(await _store.GetCachedResponseAsync("https://example.com/api/other"));
    }

    // -------------------------------------------------------------------------
    // ResetAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ResetAsync_ClearsAllData()
    {
        await _store.PutCachedResponseAsync(Cached("https://example.com/api/a"));
        await _store.UpsertQueuedWriteAsync(
            new QueuedWrite { Url = "https://example.com/api/b", Method = "POST" });

        await _store.ResetAsync();

        Assert.Empty(await _store.GetPendingOutboxAsync());
        Assert.Null(await _store.GetCachedResponseAsync("https://example.com/api/a"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CachedResponse Cached(string url) =>
        new()
        {
            Url = url,
            StatusCode = 200,
            Body = "{}"u8.ToArray(),
            CachedAt = DateTimeOffset.UtcNow,
        };

    // -------------------------------------------------------------------------
    // Binary bodies (issue #25)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task BinaryRequestBody_SurvivesTheStore()
    {
        // Cabinet serialises through System.Text.Json, which encodes byte[] as base64 — but
        // that is worth pinning rather than assuming, since it is the layer between an
        // in-memory byte[] and the bytes actually going back on the wire.
        byte[] payload = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x80, 0x81, 0xFF, 0xFE, 0x00, 0x01];

        await _store.UpsertQueuedWriteAsync(new QueuedWrite
        {
            Url = "https://example.com/api/assets",
            Method = "POST",
            RequestBody = payload,
        });

        var restored = Assert.Single(await _store.GetPendingOutboxAsync());
        Assert.Equal(payload, restored.RequestBody);
    }

    [Fact]
    public async Task BinaryResponseBody_SurvivesTheStore()
    {
        byte[] payload = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x80, 0x00, 0xFD];

        await _store.PutCachedResponseAsync(new CachedResponse
        {
            Url = "https://example.com/api/logo",
            StatusCode = 200,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "image/jpeg" },
            Body = payload,
            CachedAt = DateTimeOffset.UtcNow,
        });

        var restored = await _store.GetCachedResponseAsync("https://example.com/api/logo");

        Assert.Equal(payload, restored!.Body);
        Assert.Equal("image/jpeg", restored.Headers["Content-Type"]);
    }

    // -------------------------------------------------------------------------
    // Serialisation (issue 53)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FullQueuedWrite_SurvivesReopeningTheStore()
    {
        // Reopens the store rather than reading back through the same instance, which is the
        // whole point: RecordSet keeps an in-memory copy, so a same-instance round trip proves
        // the write and nothing about the read. A second CabinetStore over the same directory
        // has to decrypt and deserialise, so this is what exercises HyperwycJsonContext.
        //
        // Every branch of the persisted graph is populated deliberately — a type missing from
        // the context surfaces as a null or a default rather than as an exception, so a test
        // that checked only the top-level fields would pass with the nested types absent.
        var write = new QueuedWrite
        {
            Url             = "https://example.com/api/orders",
            Method          = "POST",
            ClientName      = "orders",
            RequestHeaders  = new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
            RequestBody     = [0x7B, 0x00, 0xFF, 0x7D],
            CreatedUtc      = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.FromHours(11)),
            LastOutcome     = new DeliveryOutcome
            {
                Kind            = DeliveryOutcomeKind.TransportFailure,
                StatusCode      = 422,
                ReasonPhrase    = "Unprocessable Content",
                Body            = [0x04, 0x05],
                BodyTruncated   = true,
                Error           = "no response",
                OccurredUtc     = new DateTimeOffset(2026, 3, 1, 9, 32, 0, TimeSpan.Zero),
            },
        };

        await _store.UpsertQueuedWriteAsync(write);

        var reopened = new CabinetStore(_tempDir, TestKey);
        var restored = Assert.Single(await reopened.GetPendingOutboxAsync());

        Assert.Equal(write.Id, restored.Id);
        Assert.Equal(write.CorrelationId, restored.CorrelationId);
        Assert.Equal("POST", restored.Method);
        Assert.Equal("orders", restored.ClientName);
        Assert.Equal("Bearer token", restored.RequestHeaders["Authorization"]);
        Assert.Equal(write.RequestBody, restored.RequestBody);
        Assert.Equal(write.CreatedUtc, restored.CreatedUtc);

        Assert.Equal(DeliveryOutcomeKind.TransportFailure, restored.LastOutcome!.Kind);
        Assert.Equal(422, restored.LastOutcome.StatusCode);
        Assert.Equal("Unprocessable Content", restored.LastOutcome.ReasonPhrase);
        Assert.Equal(write.LastOutcome.Body, restored.LastOutcome.Body);
        Assert.True(restored.LastOutcome.BodyTruncated);
        Assert.Equal("no response", restored.LastOutcome.Error);
        Assert.Equal(write.LastOutcome.OccurredUtc, restored.LastOutcome.OccurredUtc);
    }

    [Fact]
    public async Task FullCachedResponse_SurvivesReopeningTheStore()
    {
        var cached = new CachedResponse
        {
            Url         = "https://example.com/api/orders",
            StatusCode  = 201,
            Headers     = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body        = [0x01, 0x02, 0x03],
            CachedAt    = new DateTimeOffset(2026, 3, 1, 9, 31, 0, TimeSpan.Zero),
        };

        await _store.PutCachedResponseAsync(cached);

        var reopened = new CabinetStore(_tempDir, TestKey);
        var restored = await reopened.GetCachedResponseAsync(cached.Url);

        Assert.NotNull(restored);
        Assert.Equal(cached.Url, restored.Url);
        Assert.Equal(201, restored.StatusCode);
        Assert.Equal("application/json", restored.Headers["Content-Type"]);
        Assert.Equal(cached.Body, restored.Body);
        Assert.Equal(cached.CachedAt, restored.CachedAt);
    }

    [Fact]
    public async Task TheTwoKindsStaySeparate_AcrossAReopen()
    {
        // Same URL, both kinds. They used to share an id space, which is why cache ids carried a
        // deterministic "cache:" prefix to keep them from colliding; separate record sets make
        // that structural. See issue 55.
        const string url = "https://example.com/api/orders";

        await _store.PutCachedResponseAsync(Cached(url));
        var queued = new QueuedWrite { Url = url, Method = "POST" };
        await _store.UpsertQueuedWriteAsync(queued);

        var reopened = new CabinetStore(_tempDir, TestKey);

        Assert.Equal(queued.Id, Assert.Single(await reopened.GetPendingOutboxAsync()).Id);
        Assert.NotNull(await reopened.GetCachedResponseAsync(url));
    }

    // -------------------------------------------------------------------------
    // Bodies live outside the record document (issue 70)
    // -------------------------------------------------------------------------

    // One document per record set — a body inline would be re-encrypted and rewritten on every
    // subsequent write to the same set.
    private string CacheDocument => Path.Combine(_tempDir, "records", "CachedResponse.dat");

    private string[] AttachmentBlobs()
    {
        var dir = Path.Combine(_tempDir, "attachments");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.bin", SearchOption.AllDirectories)
            : [];
    }

    [Fact]
    public async Task CachedBody_DoesNotLandInTheRecordDocument()
    {
        // The point of the whole change, asserted on the artefact rather than on behaviour:
        // RecordSet rewrites this one file on every single-record write, so anything large in it
        // is paid for again on every subsequent write. A 256 KB body inline would be ~341 KB of
        // base64 in here; the metadata alone is a few hundred bytes.
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        await _store.PutCachedResponseAsync(new CachedResponse
        {
            Url        = "https://example.com/api/large",
            StatusCode = 200,
            Body       = payload,
            CachedAt   = DateTimeOffset.UtcNow,
        });

        var documentSize = new FileInfo(CacheDocument).Length;

        Assert.True(documentSize < 8 * 1024,
            $"record document is {documentSize} bytes; the body should not be in it");

        // And the bytes are somewhere — this is not a test that passes by losing them.
        var blob = Assert.Single(AttachmentBlobs());
        Assert.True(new FileInfo(blob).Length >= payload.Length);

        var restored = await new CabinetStore(_tempDir, TestKey)
            .GetCachedResponseAsync("https://example.com/api/large");
        Assert.Equal(payload, restored!.Body);
    }

    [Fact]
    public async Task EmptyBody_StaysDistinctFromNoBody()
    {
        // byte[0] and null mean different things — an empty 204 body versus a request that never
        // had one — and an attachment that exists but is zero length is how the difference
        // survives. Easy to lose to a "if (body.Length == 0) skip" shortcut.
        await _store.PutCachedResponseAsync(new CachedResponse
        {
            Url        = "https://example.com/api/empty",
            StatusCode = 204,
            Body       = [],
            CachedAt   = DateTimeOffset.UtcNow,
        });

        await _store.UpsertQueuedWriteAsync(
            new QueuedWrite { Url = "https://example.com/api/bodyless", Method = "DELETE" });

        var reopened = new CabinetStore(_tempDir, TestKey);

        var restored = await reopened.GetCachedResponseAsync("https://example.com/api/empty");
        Assert.NotNull(restored!.Body);
        Assert.Empty(restored.Body);

        Assert.Null(Assert.Single(await reopened.GetPendingOutboxAsync()).RequestBody);
    }

    [Fact]
    public async Task UpsertQueuedWriteAsync_BodyThatBecomesAbsent_TakesItsBlobWithIt()
    {
        var write = new QueuedWrite
        {
            Url = "https://example.com/api/orders",
            Method = "POST",
            RequestBody = [1, 2, 3],
        };
        await _store.UpsertQueuedWriteAsync(write);
        Assert.Single(AttachmentBlobs());

        // Same id, no request body. The record survives; the bytes must not.
        await _store.UpsertQueuedWriteAsync(new QueuedWrite
        {
            Id         = write.Id,
            Url        = write.Url,
            Method     = write.Method,
            CreatedUtc = write.CreatedUtc,
        });

        Assert.Empty(AttachmentBlobs());
        Assert.Null(Assert.Single(await _store.GetPendingOutboxAsync()).RequestBody);
    }

    // -------------------------------------------------------------------------
    // Invalidation removes the record and its bytes (issue 68)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_RemovesTheRecordAndItsBody()
    {
        // Invalidation used to null the response and write the husk back — a record no query
        // returns and nothing deletes. Measured against the empty-store baseline, because "no
        // longer reachable" and "no longer there" are exactly the two things the old behaviour
        // could not tell apart.
        var baseline = new FileInfo(CacheDocument).Exists
            ? new FileInfo(CacheDocument).Length
            : 0;

        for (var i = 0; i < 20; i++)
            await _store.PutCachedResponseAsync(Cached($"https://example.com/api/notes/{i}"));

        var populated = new FileInfo(CacheDocument).Length;
        Assert.Equal(20, AttachmentBlobs().Length);

        await _store.InvalidateCacheForPrefixAsync("https://example.com/api/notes");

        var afterwards = new FileInfo(CacheDocument).Length;

        Assert.True(afterwards < baseline + (populated - baseline) / 4,
            $"document is {afterwards} bytes against a {baseline}-byte empty store and {populated} populated — tombstones remain");
        Assert.Empty(AttachmentBlobs());
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_LeavesQueuedWritesAndTheirBodiesAlone()
    {
        var queued = new QueuedWrite
        {
            Url = "https://example.com/api/notes",
            Method = "POST",
            RequestBody = [9, 9, 9],
        };
        await _store.UpsertQueuedWriteAsync(queued);
        await _store.PutCachedResponseAsync(Cached("https://example.com/api/notes/1"));

        await _store.InvalidateCacheForPrefixAsync("https://example.com/api/notes");

        var pending = Assert.Single(await _store.GetPendingOutboxAsync());
        Assert.Equal(queued.Id, pending.Id);
        Assert.Equal(new byte[] { 9, 9, 9 }, pending.RequestBody);
        Assert.Single(AttachmentBlobs());
    }

    [Fact]
    public async Task ResetAsync_ClearsTheAttachmentTree()
    {
        // 2.0 nests attachments one directory per record, so the old top-level file sweep missed
        // every blob. Harmless while nothing wrote attachments; now it would leave the reset
        // holding precisely the bytes worth clearing.
        await _store.PutCachedResponseAsync(Cached("https://example.com/api/notes/1"));
        Assert.NotEmpty(AttachmentBlobs());

        await _store.ResetAsync();

        Assert.Empty(AttachmentBlobs());
        Assert.Null(await _store.GetCachedResponseAsync("https://example.com/api/notes/1"));
    }
}
