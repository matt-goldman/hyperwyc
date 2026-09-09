using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

public class InMemoryStoreTests
{
    private static Envelope MakeEnvelope(
        string url = "https://example.com/api/items",
        string method = "GET",
        DateTimeOffset? createdUtc = null) =>
        new()
        {
            Url = url,
            Method = method,
            CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow,
        };

    private static CachedResponse MakeCachedResponse() =>
        new() { StatusCode = 200, CachedAt = DateTimeOffset.UtcNow };

    // -------------------------------------------------------------------------
    // GetCachedResponseAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCachedResponseAsync_EmptyStore_ReturnsNull()
    {
        var store = new InMemoryStore();
        var result = await store.GetCachedResponseAsync("https://example.com/api/items");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedResponseAsync_EnvelopeHasNoResponse_ReturnsNull()
    {
        var store = new InMemoryStore();
        var envelope = MakeEnvelope();
        await store.UpsertAsync(envelope);

        var result = await store.GetCachedResponseAsync(envelope.Url);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedResponseAsync_EnvelopeHasResponse_ReturnsIt()
    {
        var store = new InMemoryStore();
        var envelope = MakeEnvelope();
        envelope.Response = MakeCachedResponse();
        await store.UpsertAsync(envelope);

        var result = await store.GetCachedResponseAsync(envelope.Url);

        Assert.NotNull(result);
        Assert.Equal(envelope.Id, result.Id);
    }

    [Fact]
    public async Task GetCachedResponseAsync_UrlNotMatching_ReturnsNull()
    {
        var store = new InMemoryStore();
        var envelope = MakeEnvelope("https://example.com/api/items");
        envelope.Response = MakeCachedResponse();
        await store.UpsertAsync(envelope);

        var result = await store.GetCachedResponseAsync("https://example.com/api/other");

        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // GetPendingOutboxAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPendingOutboxAsync_EmptyStore_ReturnsEmpty()
    {
        var store = new InMemoryStore();
        var result = await store.GetPendingOutboxAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_ReturnsPendingEnvelopes()
    {
        var store = new InMemoryStore();
        var e1 = MakeEnvelope();
        var e2 = MakeEnvelope();
        await store.UpsertAsync(e1);
        await store.UpsertAsync(e2);

        var result = await store.GetPendingOutboxAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_ExcludesSynced()
    {
        var store = new InMemoryStore();
        var e1 = MakeEnvelope();
        var e2 = MakeEnvelope();
        e1.IsSynced = true;
        await store.UpsertAsync(e1);
        await store.UpsertAsync(e2);

        var result = await store.GetPendingOutboxAsync();

        Assert.Single(result);
        Assert.Equal(e2.Id, result[0].Id);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_OrderedByCreatedUtc()
    {
        var store = new InMemoryStore();
        var now = DateTimeOffset.UtcNow;
        var e1 = MakeEnvelope(createdUtc: now.AddSeconds(2));
        var e2 = MakeEnvelope(createdUtc: now);
        var e3 = MakeEnvelope(createdUtc: now.AddSeconds(1));
        await store.UpsertAsync(e1);
        await store.UpsertAsync(e2);
        await store.UpsertAsync(e3);

        var result = await store.GetPendingOutboxAsync();

        Assert.Equal([e2.Id, e3.Id, e1.Id], result.Select(e => e.Id));
    }

    // -------------------------------------------------------------------------
    // GetReadyToSendAsync

    // -------------------------------------------------------------------------
    // UpsertAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_Insert_StoresEnvelope()
    {
        var store = new InMemoryStore();
        var envelope = MakeEnvelope();
        await store.UpsertAsync(envelope);

        var result = await store.GetCachedResponseAsync(envelope.Url);
        // envelope has no Response, so check via outbox instead
        var outbox = await store.GetPendingOutboxAsync();
        Assert.Single(outbox);
        Assert.Equal(envelope.Id, outbox[0].Id);
    }

    [Fact]
    public async Task UpsertAsync_Update_ReplacesExistingById()
    {
        var store = new InMemoryStore();
        var envelope = MakeEnvelope();
        await store.UpsertAsync(envelope);

        envelope.Response = MakeCachedResponse();
        await store.UpsertAsync(envelope);

        var cached = await store.GetCachedResponseAsync(envelope.Url);
        Assert.NotNull(cached?.Response);
    }

    // -------------------------------------------------------------------------
    // RemoveDeliveredAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RemoveDeliveredAsync_ExistingId_TakesItOutOfTheOutbox()
    {
        var store = new InMemoryStore();
        var envelope = MakeEnvelope();
        await store.UpsertAsync(envelope);

        await store.RemoveDeliveredAsync(envelope.Id);

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task RemoveDeliveredAsync_ExistingId_DiscardsTheRecordEntirely()
    {
        // Not merely absent from the outbox — absent. A flag would satisfy the test above
        // while still holding the request body and its Authorization header. See ADR 0010.
        var store = new InMemoryStore();
        var envelope = MakeEnvelope();
        envelope.Response = MakeCachedResponse();
        await store.UpsertAsync(envelope);

        await store.RemoveDeliveredAsync(envelope.Id);

        Assert.Null(await store.GetCachedResponseAsync(envelope.Url));
    }

    [Fact]
    public async Task RemoveDeliveredAsync_MissingId_DoesNotThrow()
    {
        var store = new InMemoryStore();
        await store.RemoveDeliveredAsync("nonexistent-id"); // should not throw
    }

    // -------------------------------------------------------------------------
    // InvalidateCacheForPrefixAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_MatchingUrl_RemovesEntry()
    {
        var store = new InMemoryStore();
        var envelope = MakeEnvelope("https://example.com/api/items");
        envelope.Response = MakeCachedResponse();
        await store.UpsertAsync(envelope);

        await store.InvalidateCacheForPrefixAsync("https://example.com/api/");

        var result = await store.GetCachedResponseAsync(envelope.Url);
        Assert.Null(result);
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_NonMatchingUrl_PreservesEntry()
    {
        var store = new InMemoryStore();
        var envelope = MakeEnvelope("https://example.com/api/items");
        envelope.Response = MakeCachedResponse();
        await store.UpsertAsync(envelope);

        await store.InvalidateCacheForPrefixAsync("https://other.com/");

        var result = await store.GetCachedResponseAsync(envelope.Url);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_OnlyInvalidatesMatchingUrls()
    {
        var store = new InMemoryStore();
        var e1 = MakeEnvelope("https://example.com/api/items");
        e1.Response = MakeCachedResponse();
        var e2 = MakeEnvelope("https://example.com/other/stuff");
        e2.Response = MakeCachedResponse();
        await store.UpsertAsync(e1);
        await store.UpsertAsync(e2);

        await store.InvalidateCacheForPrefixAsync("https://example.com/api/");

        Assert.Null(await store.GetCachedResponseAsync(e1.Url));
        Assert.NotNull(await store.GetCachedResponseAsync(e2.Url));
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_LeavesQueuedWritesAlone()
    {
        // The loop used to match on the URL alone, so a POST /sales that invalidated /sales also
        // swept up every queued write under the same prefix. Nulling an already-null Response made
        // that harmless; removing the record does not, so the filter is now load-bearing rather
        // than merely wasteful. See issue #68.
        var store = new InMemoryStore();

        var queued = MakeEnvelope("https://example.com/api/items", method: "POST");
        await store.UpsertAsync(queued);

        var cached = MakeEnvelope("https://example.com/api/items/1");
        cached.IsSynced = true;
        cached.Response = MakeCachedResponse();
        await store.UpsertAsync(cached);

        await store.InvalidateCacheForPrefixAsync("https://example.com/api/items");

        Assert.Null(await store.GetCachedResponseAsync(cached.Url));
        Assert.Equal(queued.Id, Assert.Single(await store.GetPendingOutboxAsync()).Id);
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_EmptyStore_DoesNotThrow()
    {
        var store = new InMemoryStore();
        await store.InvalidateCacheForPrefixAsync("https://example.com/"); // should not throw
    }

    // -------------------------------------------------------------------------
    // ResetAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ResetAsync_ClearsAllEnvelopes()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(MakeEnvelope("https://example.com/a"));
        await store.UpsertAsync(MakeEnvelope("https://example.com/b"));

        await store.ResetAsync();

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task ResetAsync_EmptyStore_DoesNotThrow()
    {
        var store = new InMemoryStore();
        await store.ResetAsync(); // should not throw
    }
}
