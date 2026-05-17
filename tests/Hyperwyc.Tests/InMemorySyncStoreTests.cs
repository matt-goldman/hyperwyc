using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

public class InMemorySyncStoreTests
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
        var store = new InMemorySyncStore();
        var result = await store.GetCachedResponseAsync("https://example.com/api/items");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedResponseAsync_EnvelopeHasNoResponse_ReturnsNull()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        await store.UpsertAsync(envelope);

        var result = await store.GetCachedResponseAsync(envelope.Url);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedResponseAsync_EnvelopeHasResponse_ReturnsIt()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        envelope.Response = MakeCachedResponse();
        await store.UpsertAsync(envelope);

        var result = await store.GetCachedResponseAsync(envelope.Url);

        Assert.NotNull(result);
        Assert.Equal(envelope.Id, result.Id);
    }

    [Fact]
    public async Task GetCachedResponseAsync_DeadLetteredEnvelope_ReturnsNull()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        envelope.Response = MakeCachedResponse();
        envelope.IsDeadLettered = true;
        await store.UpsertAsync(envelope);

        var result = await store.GetCachedResponseAsync(envelope.Url);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedResponseAsync_UrlNotMatching_ReturnsNull()
    {
        var store = new InMemorySyncStore();
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
        var store = new InMemorySyncStore();
        var result = await store.GetPendingOutboxAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_ReturnsPendingEnvelopes()
    {
        var store = new InMemorySyncStore();
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
        var store = new InMemorySyncStore();
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
    public async Task GetPendingOutboxAsync_ExcludesDeadLettered()
    {
        var store = new InMemorySyncStore();
        var e1 = MakeEnvelope();
        var e2 = MakeEnvelope();
        e1.IsDeadLettered = true;
        await store.UpsertAsync(e1);
        await store.UpsertAsync(e2);

        var result = await store.GetPendingOutboxAsync();

        Assert.Single(result);
        Assert.Equal(e2.Id, result[0].Id);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_OrderedByCreatedUtc()
    {
        var store = new InMemorySyncStore();
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
    // GetDueForRetryAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetDueForRetryAsync_EmptyStore_ReturnsEmpty()
    {
        var store = new InMemorySyncStore();
        var result = await store.GetDueForRetryAsync(DateTimeOffset.UtcNow);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDueForRetryAsync_NullNextRetryUtc_NotIncluded()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        await store.UpsertAsync(envelope);

        var result = await store.GetDueForRetryAsync(DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDueForRetryAsync_RetryInFuture_NotIncluded()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        envelope.NextRetryUtc = DateTimeOffset.UtcNow.AddHours(1);
        await store.UpsertAsync(envelope);

        var result = await store.GetDueForRetryAsync(DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDueForRetryAsync_RetryDue_Included()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        var now = DateTimeOffset.UtcNow;
        envelope.NextRetryUtc = now.AddMinutes(-5);
        await store.UpsertAsync(envelope);

        var result = await store.GetDueForRetryAsync(now);

        Assert.Single(result);
        Assert.Equal(envelope.Id, result[0].Id);
    }

    [Fact]
    public async Task GetDueForRetryAsync_ExcludesSynced()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        envelope.NextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        envelope.IsSynced = true;
        await store.UpsertAsync(envelope);

        var result = await store.GetDueForRetryAsync(DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetDueForRetryAsync_ExcludesDeadLettered()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        envelope.NextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        envelope.IsDeadLettered = true;
        await store.UpsertAsync(envelope);

        var result = await store.GetDueForRetryAsync(DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    // -------------------------------------------------------------------------
    // UpsertAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_Insert_StoresEnvelope()
    {
        var store = new InMemorySyncStore();
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
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        await store.UpsertAsync(envelope);

        envelope.Response = MakeCachedResponse();
        await store.UpsertAsync(envelope);

        var cached = await store.GetCachedResponseAsync(envelope.Url);
        Assert.NotNull(cached?.Response);
    }

    // -------------------------------------------------------------------------
    // MarkSyncedAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MarkSyncedAsync_ExistingId_SetsSynced()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        await store.UpsertAsync(envelope);

        await store.MarkSyncedAsync(envelope.Id);

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task MarkSyncedAsync_MissingId_DoesNotThrow()
    {
        var store = new InMemorySyncStore();
        await store.MarkSyncedAsync("nonexistent-id"); // should not throw
    }

    // -------------------------------------------------------------------------
    // MoveToDeadLetterAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MoveToDeadLetterAsync_ExistingId_SetsDeadLettered()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope();
        await store.UpsertAsync(envelope);

        await store.MoveToDeadLetterAsync(envelope.Id);

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task MoveToDeadLetterAsync_MissingId_DoesNotThrow()
    {
        var store = new InMemorySyncStore();
        await store.MoveToDeadLetterAsync("nonexistent-id"); // should not throw
    }

    // -------------------------------------------------------------------------
    // InvalidateCacheForPrefixAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_MatchingUrl_ClearsResponse()
    {
        var store = new InMemorySyncStore();
        var envelope = MakeEnvelope("https://example.com/api/items");
        envelope.Response = MakeCachedResponse();
        await store.UpsertAsync(envelope);

        await store.InvalidateCacheForPrefixAsync("https://example.com/api/");

        var result = await store.GetCachedResponseAsync(envelope.Url);
        Assert.Null(result);
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_NonMatchingUrl_PreservesResponse()
    {
        var store = new InMemorySyncStore();
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
        var store = new InMemorySyncStore();
        var e1 = MakeEnvelope("https://example.com/api/items");
        e1.Response = MakeCachedResponse();
        var e2 = MakeEnvelope("https://example.com/other/stuff");
        e2.Response = MakeCachedResponse();
        await store.UpsertAsync(e1);
        await store.UpsertAsync(e2);

        await store.InvalidateCacheForPrefixAsync("https://example.com/api/");

        Assert.Null((await store.GetCachedResponseAsync(e1.Url))?.Response);
        Assert.NotNull((await store.GetCachedResponseAsync(e2.Url))?.Response);
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_EmptyStore_DoesNotThrow()
    {
        var store = new InMemorySyncStore();
        await store.InvalidateCacheForPrefixAsync("https://example.com/"); // should not throw
    }

    // -------------------------------------------------------------------------
    // ResetAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ResetAsync_ClearsAllEnvelopes()
    {
        var store = new InMemorySyncStore();
        await store.UpsertAsync(MakeEnvelope("https://example.com/a"));
        await store.UpsertAsync(MakeEnvelope("https://example.com/b"));

        await store.ResetAsync();

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task ResetAsync_EmptyStore_DoesNotThrow()
    {
        var store = new InMemorySyncStore();
        await store.ResetAsync(); // should not throw
    }
}
