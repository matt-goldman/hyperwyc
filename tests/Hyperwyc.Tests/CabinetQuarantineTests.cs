using Hyperwyc.Cabinet;
using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue 62: an unreadable store is moved aside rather than left in place, so caching and
/// queueing resume — and moved rather than deleted, so the bytes survive for anyone who can do
/// something with them.
/// </summary>
/// <remarks>
/// The behaviour <see cref="CabinetResetTests"/> covers is the consumer asking to discard. This
/// is Hyperwyc deciding on its own, which is a much higher bar: it may not destroy anything, it
/// may not do it twice, and what it leaves behind has to still be openable. The last of those is
/// the trap — the default key is derived from the store's own path, so moving the files without
/// moving the derivation would make both stores unreadable.
/// </remarks>
public sealed class CabinetQuarantineTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"hyperwyc-quarantine-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private CabinetStoreOptions Options(byte[]? key = null) =>
        new() { DirectoryPath = _dir, EncryptionKey = key };

    private static QueuedWrite Queued(string url = "https://example.com/api/sales") =>
        new() { Url = url, Method = "POST" };

    // -------------------------------------------------------------------------
    // The store resumes
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Quarantine_LeavesAStoreThatWorks()
    {
        var store = new CabinetStore(Options());
        await store.UpsertQueuedWriteAsync(Queued());

        Assert.True(await store.TryQuarantineAsync());

        Assert.Empty(await store.GetPendingOutboxAsync());

        // The point of the whole exercise: queueing works again, on the same instance, without a
        // restart and without the consumer being asked to do anything.
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/returns"));
        Assert.Single(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task Quarantine_ClearsTheCacheToo()
    {
        var store = new CabinetStore(Options());
        await store.PutCachedResponseAsync(new CachedResponse
        {
            Url = "https://example.com/api/products",
            StatusCode = 200,
            Body = [1, 2, 3],
        });

        Assert.True(await store.TryQuarantineAsync());

        Assert.Null(await store.GetCachedResponseAsync("https://example.com/api/products"));
    }

    [Fact]
    public async Task Quarantine_RecoversAStoreThatCannotBeDecrypted()
    {
        // The real trigger, not a simulation of one: written under one key and opened under
        // another, which is what a moved directory or a restored backup looks like.
        var written = new CabinetStore(_dir, new byte[32]);
        await written.UpsertQueuedWriteAsync(Queued());

        var otherKey = new byte[32];
        otherKey[0] = 0xFF;
        var unreadable = new CabinetStore(_dir, otherKey);
        await Assert.ThrowsAnyAsync<Exception>(() => unreadable.GetPendingOutboxAsync());

        Assert.True(await unreadable.TryQuarantineAsync());

        Assert.Empty(await unreadable.GetPendingOutboxAsync());
        await unreadable.UpsertQueuedWriteAsync(Queued());
        Assert.Single(await unreadable.GetPendingOutboxAsync());
    }

    // -------------------------------------------------------------------------
    // Nothing is destroyed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Quarantine_KeepsTheOldStoreOnDisk()
    {
        var store = new CabinetStore(Options());
        await store.UpsertQueuedWriteAsync(Queued());

        await store.TryQuarantineAsync();

        var quarantine = CabinetStore.QuarantinePath(Options());
        Assert.True(Directory.Exists(quarantine));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(quarantine, "records")));
    }

    [Fact]
    public async Task TheQuarantinedStore_StillOpensUnderTheDerivedKey()
    {
        // The trap this whole API exists for. The default key is SHA-256 of the store's own
        // directory path, and the quarantined bytes were written under the key derived from the
        // *original* path — so opening the quarantine directory as an ordinary store derives a
        // different key and finds it unreadable, which looks exactly like the damage that caused
        // the quarantine. OpenQuarantined is what carries the derivation across the move.
        var store = new CabinetStore(Options());
        await store.UpsertQueuedWriteAsync(Queued());
        await store.TryQuarantineAsync();

        var quarantined = CabinetStore.OpenQuarantined(Options());

        Assert.NotNull(quarantined);
        var recovered = Assert.Single(await quarantined.GetPendingOutboxAsync());
        Assert.Equal("https://example.com/api/sales", recovered.Url);

        // And the wrong way round still fails, which is why the right way round needs a method.
        var naive = new CabinetStore(CabinetStore.QuarantinePath(Options()));
        await Assert.ThrowsAnyAsync<Exception>(() => naive.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task TheQuarantinedStore_StillOpensUnderAnExplicitKey()
    {
        var key = new byte[32];
        key[7] = 0x5A;

        var store = new CabinetStore(Options(key));
        await store.UpsertQueuedWriteAsync(Queued());
        await store.TryQuarantineAsync();

        var quarantined = CabinetStore.OpenQuarantined(Options(key));

        Assert.NotNull(quarantined);
        Assert.Single(await quarantined.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task TheQuarantinedStore_KeepsRequestBodies()
    {
        // Bodies are attachments rather than record fields (issue #70), so they live in a
        // directory of their own. A move that took the records and left the blobs would leave
        // something that deserialises and says nothing.
        var store = new CabinetStore(Options());
        await store.UpsertQueuedWriteAsync(new QueuedWrite
        {
            Url = "https://example.com/api/sales",
            Method = "POST",
            RequestBody = "the payload"u8.ToArray(),
        });

        await store.TryQuarantineAsync();

        var quarantined = CabinetStore.OpenQuarantined(Options());
        var recovered = Assert.Single(await quarantined!.GetPendingOutboxAsync());
        Assert.Equal("the payload", recovered.GetRequestBodyAsText());
    }

    // -------------------------------------------------------------------------
    // Once, and only once
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ASecondQuarantine_IsDeclined()
    {
        // The existence of the quarantine is the counter. Storage is bounded at 2× with no
        // configuration, no sweep and no scheduler — and the first orphan, usually the more
        // diagnostic one, is not overwritten by the second.
        var store = new CabinetStore(Options());
        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/first"));

        Assert.True(await store.TryQuarantineAsync());

        await store.UpsertQueuedWriteAsync(Queued("https://example.com/api/second"));

        Assert.False(await store.TryQuarantineAsync());

        // Declining means declining: the second store is untouched, not silently cleared.
        Assert.Single(await store.GetPendingOutboxAsync());

        var quarantined = CabinetStore.OpenQuarantined(Options());
        var kept = Assert.Single(await quarantined!.GetPendingOutboxAsync());
        Assert.Equal("https://example.com/api/first", kept.Url);
    }

    [Fact]
    public void OpenQuarantined_IsNullWhenThereIsNone_AndCreatesNothing()
    {
        // Probing must not leave a directory behind: its existence is what stops a second
        // quarantine, so a diagnostics screen asking "is there one?" would otherwise disable
        // the feature by asking.
        _ = new CabinetStore(Options());

        Assert.Null(CabinetStore.OpenQuarantined(Options()));
        Assert.False(Directory.Exists(CabinetStore.QuarantinePath(Options())));
    }

    // -------------------------------------------------------------------------
    // Reset still discards
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Reset_DiscardsTheQuarantineToo()
    {
        // Reset is the consumer saying discard, and the case it exists for is logout. Leaving
        // the previous user's queued writes sitting in a quarantine directory is the opposite of
        // what was asked.
        var store = new CabinetStore(Options());
        await store.UpsertQueuedWriteAsync(Queued());
        await store.TryQuarantineAsync();

        await store.ResetAsync();

        Assert.False(Directory.Exists(CabinetStore.QuarantinePath(Options())));
        Assert.Null(CabinetStore.OpenQuarantined(Options()));
    }

    [Fact]
    public async Task Reset_LetsTheStoreBeQuarantinedAgain()
    {
        // The counter goes with the contents. A consumer who has recovered what they wanted and
        // reset is starting over, and the next failure deserves the same treatment as the first.
        var store = new CabinetStore(Options());
        await store.TryQuarantineAsync();
        await store.ResetAsync();

        Assert.True(await store.TryQuarantineAsync());
    }
}
