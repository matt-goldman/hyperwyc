using Hyperwyc.Cabinet;
using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Cabinet.Tests;

/// <summary>
/// Integration tests for <see cref="CabinetSyncStore"/> using a real Cabinet
/// in-process instance backed by a temporary directory.
/// </summary>
public class CabinetSyncStoreTests : IDisposable
{
    // A fixed 32-byte test key (NOT for production use).
    private static readonly byte[] TestKey =
        System.Security.Cryptography.SHA256.HashData("Hyperwyc-test"u8.ToArray());

    private readonly string _tempDir;
    private readonly CabinetSyncStore _store;

    public CabinetSyncStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Hyperwyc-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
        _store = new CabinetSyncStore(_tempDir, TestKey);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // -------------------------------------------------------------------------
    // UpsertAsync / GetCachedResponseAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_NewEnvelope_CanBeRetrievedByCachedResponseAsync()
    {
        var envelope = MakeCachedEnvelope("https://example.com/api/items");
        await _store.UpsertAsync(envelope);

        var result = await _store.GetCachedResponseAsync("https://example.com/api/items");

        Assert.NotNull(result);
        Assert.Equal(200, result!.Response!.StatusCode);
    }

    [Fact]
    public async Task UpsertAsync_UpdatesExistingEnvelope()
    {
        var envelope = MakeCachedEnvelope("https://example.com/api/items");
        await _store.UpsertAsync(envelope);

        // Replace the response and upsert again.
        envelope.Response = new CachedResponse { StatusCode = 200, Body = "updated", CachedAt = DateTimeOffset.UtcNow };
        await _store.UpsertAsync(envelope);

        var result = await _store.GetCachedResponseAsync("https://example.com/api/items");
        Assert.Equal("updated", result!.Response!.Body);
    }

    [Fact]
    public async Task GetCachedResponseAsync_NoMatch_ReturnsNull()
    {
        var result = await _store.GetCachedResponseAsync("https://example.com/api/missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedResponseAsync_DeadLetteredEnvelope_ReturnsNull()
    {
        var envelope = MakeCachedEnvelope("https://example.com/api/items");
        envelope.IsDeadLettered = true;
        await _store.UpsertAsync(envelope);

        var result = await _store.GetCachedResponseAsync("https://example.com/api/items");
        Assert.Null(result);
    }

    // -------------------------------------------------------------------------
    // GetPendingOutboxAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetPendingOutboxAsync_ReturnsPendingEnvelopesInOrder()
    {
        var first  = new Envelope { Url = "https://example.com/a", Method = "POST" };
        await Task.Delay(5);
        var second = new Envelope { Url = "https://example.com/b", Method = "POST" };
        await Task.Delay(5);
        var third  = new Envelope { Url = "https://example.com/c", Method = "POST" };

        // Insert in reverse order to verify ordering.
        await _store.UpsertAsync(third);
        await _store.UpsertAsync(first);
        await _store.UpsertAsync(second);

        var pending = await _store.GetPendingOutboxAsync();

        Assert.Equal(3, pending.Count);
        Assert.Equal("https://example.com/a", pending[0].Url);
        Assert.Equal("https://example.com/b", pending[1].Url);
        Assert.Equal("https://example.com/c", pending[2].Url);
    }

    [Fact]
    public async Task GetPendingOutboxAsync_ExcludesSyncedAndDeadLettered()
    {
        var pending = new Envelope { Url = "https://example.com/pending", Method = "POST" };
        var synced = new Envelope { Url = "https://example.com/synced", Method = "POST" };
        synced.IsSynced = true;
        var dead = new Envelope { Url = "https://example.com/dead", Method = "POST" };
        dead.IsDeadLettered = true;

        await _store.UpsertAsync(pending);
        await _store.UpsertAsync(synced);
        await _store.UpsertAsync(dead);

        var result = await _store.GetPendingOutboxAsync();

        Assert.Single(result);
        Assert.Equal("https://example.com/pending", result[0].Url);
    }

    // -------------------------------------------------------------------------
    // MarkSyncedAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MarkSyncedAsync_RemovesEnvelopeFromPendingOutbox()
    {
        var envelope = new Envelope { Url = "https://example.com/api/orders", Method = "POST" };
        await _store.UpsertAsync(envelope);

        await _store.MarkSyncedAsync(envelope.Id);

        var pending = await _store.GetPendingOutboxAsync();
        Assert.Empty(pending);
    }

    // -------------------------------------------------------------------------
    // MoveToDeadLetterAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MoveToDeadLetterAsync_RemovesEnvelopeFromPendingOutbox()
    {
        var envelope = new Envelope { Url = "https://example.com/api/orders", Method = "POST" };
        await _store.UpsertAsync(envelope);

        await _store.MoveToDeadLetterAsync(envelope.Id);

        var pending = await _store.GetPendingOutboxAsync();
        Assert.Empty(pending);
    }

    // -------------------------------------------------------------------------
    // InvalidateCacheForPrefixAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InvalidateCacheForPrefixAsync_ClearsResponseOnMatchingEnvelopes()
    {
        await _store.UpsertAsync(MakeCachedEnvelope("https://example.com/api/notes"));
        await _store.UpsertAsync(MakeCachedEnvelope("https://example.com/api/notes/42"));
        await _store.UpsertAsync(MakeCachedEnvelope("https://example.com/api/other"));

        await _store.InvalidateCacheForPrefixAsync("https://example.com/api/notes");

        Assert.Null(await _store.GetCachedResponseAsync("https://example.com/api/notes"));
        Assert.Null(await _store.GetCachedResponseAsync("https://example.com/api/notes/42"));
        // Unrelated URL should be unaffected.
        Assert.NotNull(await _store.GetCachedResponseAsync("https://example.com/api/other"));
    }

    // -------------------------------------------------------------------------
    // GetReadyToSendAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetReadyToSendAsync_ReturnsEnvelopesDueBeforeNow()
    {
        var due = new Envelope
        {
            Url = "https://example.com/api/orders",
            Method = "POST",
            NextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var notDue = new Envelope
        {
            Url = "https://example.com/api/notes",
            Method = "POST",
            NextRetryUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        };

        await _store.UpsertAsync(due);
        await _store.UpsertAsync(notDue);

        var results = await _store.GetReadyToSendAsync(DateTimeOffset.UtcNow);

        Assert.Single(results);
        Assert.Equal("https://example.com/api/orders", results[0].Url);
    }

    // -------------------------------------------------------------------------
    // ResetAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ResetAsync_ClearsAllData()
    {
        await _store.UpsertAsync(MakeCachedEnvelope("https://example.com/api/a"));
        await _store.UpsertAsync(new Envelope { Url = "https://example.com/api/b", Method = "POST" });

        await _store.ResetAsync();

        var pending = await _store.GetPendingOutboxAsync();
        var cached = await _store.GetCachedResponseAsync("https://example.com/api/a");

        Assert.Empty(pending);
        Assert.Null(cached);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Envelope MakeCachedEnvelope(string url) =>
        new()
        {
            Url = url,
            Method = "GET",
            IsSynced = true,
            Response = new CachedResponse
            {
                StatusCode = 200,
                Body = "{}",
                CachedAt = DateTimeOffset.UtcNow,
            },
        };
}
