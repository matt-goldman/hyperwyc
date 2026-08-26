using Hyperwyc.Cabinet;
using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers the store being used from more than one thread at a time, which is the normal case
/// rather than an edge one: <c>HyperwycHandler</c> is transient and runs on whatever thread its
/// caller used, so two overlapping HTTP requests reach the store together, and an orchestrator
/// flush runs on a background task alongside them.
/// </summary>
/// <remarks>
/// Cabinet's <c>FileOfflineStore</c> saves by writing <c>Envelope.dat.tmp</c> and then
/// <c>File.Move</c>-ing it over <c>Envelope.dat</c>. Two saves in flight at once race: the first
/// Move consumes the temp file and the second throws <see cref="FileNotFoundException"/>. These
/// tests fail against an unsynchronised store.
/// </remarks>
public sealed class CabinetConcurrencyTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"hyperwyc-conc-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private CabinetSyncStore Store() => new(_dir);

    private static Envelope Cached(string url) => new()
    {
        Url = url,
        Method = "GET",
        IsSynced = true,
        Response = new CachedResponse
        {
            StatusCode = 200,
            Body = "[]"u8.ToArray(),
            CachedAt = DateTimeOffset.UtcNow,
        },
    };

    [Fact]
    public async Task ConcurrentUpserts_AllSucceedAndArePersisted()
    {
        var store = Store();

        // The reported crash: two overlapping GETs both caching their response.
        var writes = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => store.UpsertAsync(Cached($"https://example.com/api/{i}"))));

        await Task.WhenAll(writes);

        for (var i = 0; i < 32; i++)
        {
            Assert.NotNull(await store.GetCachedResponseAsync($"https://example.com/api/{i}"));
        }
    }

    [Fact]
    public async Task ConcurrentUpsertsOfTheSameUrl_DoNotCorruptTheStore()
    {
        var store = Store();
        var envelope = Cached("https://example.com/api/products");

        var writes = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.UpsertAsync(envelope)));

        await Task.WhenAll(writes);

        Assert.NotNull(await store.GetCachedResponseAsync("https://example.com/api/products"));
    }

    [Fact]
    public async Task ReadsAndWritesInterleaved_DoNotThrow()
    {
        var store = Store();
        await store.UpsertAsync(Cached("https://example.com/api/seed"));

        // A flush reading the outbox while the handler writes cache entries.
        var work = new List<Task>();
        for (var i = 0; i < 16; i++)
        {
            var n = i;
            work.Add(Task.Run(() => store.UpsertAsync(Cached($"https://example.com/api/{n}"))));
            work.Add(Task.Run(() => store.GetPendingOutboxAsync()));
        }

        await Task.WhenAll(work);
    }

    [Fact]
    public async Task ConcurrentUpsertAndReset_DoNotThrow()
    {
        var store = Store();
        for (var i = 0; i < 8; i++)
            await store.UpsertAsync(Cached($"https://example.com/api/{i}"));

        var work = new List<Task> { Task.Run(() => store.ResetAsync()) };
        for (var i = 8; i < 24; i++)
        {
            var n = i;
            work.Add(Task.Run(() => store.UpsertAsync(Cached($"https://example.com/api/{n}"))));
        }

        await Task.WhenAll(work);
    }

    [Fact]
    public async Task ConcurrentMarkSynced_DoesNotLoseUpdates()
    {
        var store = Store();
        var ids = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var e = new Envelope { Url = $"https://example.com/api/{i}", Method = "POST" };
            ids.Add(e.Id);
            await store.UpsertAsync(e);
        }

        await Task.WhenAll(ids.Select(id => Task.Run(() => store.MarkSyncedAsync(id))));

        // Every envelope is out of the outbox. Without serialisation these are
        // read-modify-write races and some updates are silently discarded.
        Assert.Empty(await store.GetPendingOutboxAsync());
    }
}
