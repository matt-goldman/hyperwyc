using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// The store contract, exercised against the in-memory implementation.
/// </summary>
/// <remarks>
/// Several tests here used to assert that a cache record stayed out of the outbox and a queued
/// write stayed out of the cache — both of which were properties of a filter on a shared type.
/// They are now properties of the types, and what is left to test is that the two partitions
/// really are separate. See issue 55.
/// </remarks>
public class InMemoryStoreTests
{
    private static QueuedWrite Queued(
        string url = "https://example.com/api/items",
        string method = "POST",
        DateTimeOffset? createdUtc = null) =>
        new()
        {
            Url = url,
            Method = method,
            CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow,
        };

    private static CachedResponse Cached(string url = "https://example.com/api/items") =>
        new() { Url = url, StatusCode = 200, CachedAt = DateTimeOffset.UtcNow };

    // -------------------------------------------------------------------------
    // GetCachedResponseAsync / PutCachedResponseAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetCachedResponseAsync_EmptyStore_ReturnsNull()
    {
        var store = new InMemoryStore();
        var result = await store.GetCachedResponseAsync("https://example.com/api/items");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedResponseAsync_ReturnsWhatWasPut()
    {
        var store = new InMemoryStore();
        var cached = Cached();
        await store.PutCachedResponseAsync(cached);

        var result = await store.GetCachedResponseAsync(cached.Url);

        Assert.NotNull(result);
        Assert.Equal(cached.Url, result.Url);
    }

    [Fact]
    public async Task GetCachedResponseAsync_UrlNotMatching_ReturnsNull()
    {
        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached("https://example.com/api/items"));

        var result = await store.GetCachedResponseAsync("https://example.com/api/other");

        Assert.Null(result);
    }

    [Fact]
    public async Task PutCachedResponseAsync_SameUrl_ReplacesRatherThanAppends()
    {
        // Keyed on the URL, so a refetch overwrites. When cache records carried generated ids
        // instead, this appended — and the lookup went on returning the first one stored.
        var store = new InMemoryStore();
        var url = "https://example.com/api/items";

        await store.PutCachedResponseAsync(new CachedResponse
        {
            Url = url,
            StatusCode = 200,
            Body = "first"u8.ToArray(),
            CachedAt = DateTimeOffset.UtcNow,
        });
        await store.PutCachedResponseAsync(new CachedResponse
        {
            Url = url,
            StatusCode = 200,
            Body = "second"u8.ToArray(),
            CachedAt = DateTimeOffset.UtcNow,
        });

        var result = await store.GetCachedResponseAsync(url);

        Assert.Equal("second", result?.GetBodyAsText());
    }

    [Fact]
    public async Task CachedResponses_DoNotAppearInTheOutbox()
    {
        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached());

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    // -------------------------------------------------------------------------
    // GetPendingOutboxAsync / UpsertQueuedWriteAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPendingOutboxAsync_EmptyStore_ReturnsEmpty()
    {
        var store = new InMemoryStore();
        var result = await store.GetPendingOutboxAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_ReturnsEveryQueuedWrite()
    {
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Queued());
        await store.UpsertQueuedWriteAsync(Queued());

        var result = await store.GetPendingOutboxAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_OrderedByCreatedUtc()
    {
        var store = new InMemoryStore();
        var now = DateTimeOffset.UtcNow;
        var w1 = Queued(createdUtc: now.AddSeconds(2));
        var w2 = Queued(createdUtc: now);
        var w3 = Queued(createdUtc: now.AddSeconds(1));
        await store.UpsertQueuedWriteAsync(w1);
        await store.UpsertQueuedWriteAsync(w2);
        await store.UpsertQueuedWriteAsync(w3);

        var result = await store.GetPendingOutboxAsync();

        Assert.Equal([w2.Id, w3.Id, w1.Id], result.Select(w => w.Id));
    }

    [Fact]
    public async Task UpsertQueuedWriteAsync_SameId_ReplacesRatherThanAppends()
    {
        var store = new InMemoryStore();
        var write = Queued();
        await store.UpsertQueuedWriteAsync(write);

        write.LastOutcome = new DeliveryOutcome
        {
            Kind = DeliveryOutcomeKind.TransportFailure,
            Error = "no route to host",
            OccurredUtc = DateTimeOffset.UtcNow,
        };
        await store.UpsertQueuedWriteAsync(write);

        var outbox = await store.GetPendingOutboxAsync();
        Assert.Equal("no route to host", Assert.Single(outbox).LastOutcome?.Error);
    }

    [Fact]
    public async Task QueuedWrites_DoNotAppearInTheCache()
    {
        var store = new InMemoryStore();
        var write = Queued("https://example.com/api/items");
        await store.UpsertQueuedWriteAsync(write);

        Assert.Null(await store.GetCachedResponseAsync(write.Url));
    }

    // -------------------------------------------------------------------------
    // RemoveDeliveredAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RemoveDeliveredAsync_ExistingId_TakesItOutOfTheOutbox()
    {
        var store = new InMemoryStore();
        var write = Queued();
        await store.UpsertQueuedWriteAsync(write);

        await store.RemoveDeliveredAsync(write.Id);

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task RemoveDeliveredAsync_MissingId_DoesNotThrow()
    {
        var store = new InMemoryStore();
        await store.RemoveDeliveredAsync("nonexistent-id"); // should not throw
    }

    [Fact]
    public async Task RemoveDeliveredAsync_DoesNotTouchTheCache()
    {
        // Only the outbox is its business, and the id spaces are separate — a delivered write
        // cannot take a cache entry with it however the two ids happen to be shaped.
        var store = new InMemoryStore();
        var cached = Cached();
        await store.PutCachedResponseAsync(cached);

        await store.RemoveDeliveredAsync(cached.Url);

        Assert.NotNull(await store.GetCachedResponseAsync(cached.Url));
    }

    // -------------------------------------------------------------------------
    // InvalidateCacheForPrefixAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_MatchingUrl_RemovesEntry()
    {
        var store = new InMemoryStore();
        var cached = Cached("https://example.com/api/items");
        await store.PutCachedResponseAsync(cached);

        await store.InvalidateCacheForPrefixAsync("https://example.com/api/");

        Assert.Null(await store.GetCachedResponseAsync(cached.Url));
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_NonMatchingUrl_PreservesEntry()
    {
        var store = new InMemoryStore();
        var cached = Cached("https://example.com/api/items");
        await store.PutCachedResponseAsync(cached);

        await store.InvalidateCacheForPrefixAsync("https://other.com/");

        Assert.NotNull(await store.GetCachedResponseAsync(cached.Url));
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_OnlyInvalidatesMatchingUrls()
    {
        var store = new InMemoryStore();
        var c1 = Cached("https://example.com/api/items");
        var c2 = Cached("https://example.com/other/stuff");
        await store.PutCachedResponseAsync(c1);
        await store.PutCachedResponseAsync(c2);

        await store.InvalidateCacheForPrefixAsync("https://example.com/api/");

        Assert.Null(await store.GetCachedResponseAsync(c1.Url));
        Assert.NotNull(await store.GetCachedResponseAsync(c2.Url));
    }

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_LeavesQueuedWritesAlone()
    {
        // A POST /items that invalidates /items must not sweep up the queued writes under the
        // same prefix. That took a filter on Response being non-null — a kind check written as a
        // field check — while both kinds shared a type and a partition. See issues 68 and 55.
        var store = new InMemoryStore();

        var queued = Queued("https://example.com/api/items");
        await store.UpsertQueuedWriteAsync(queued);

        var cached = Cached("https://example.com/api/items/1");
        await store.PutCachedResponseAsync(cached);

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
    public async Task ResetAsync_ClearsBothPartitions()
    {
        var store = new InMemoryStore();
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/a"));
        await store.PutCachedResponseAsync(Cached("https://example.com/b"));

        await store.ResetAsync();

        Assert.Empty(await store.GetPendingOutboxAsync());
        Assert.Null(await store.GetCachedResponseAsync("https://example.com/b"));
    }

    [Fact]
    public async Task ResetAsync_EmptyStore_DoesNotThrow()
    {
        var store = new InMemoryStore();
        await store.ResetAsync(); // should not throw
    }
}
