using Hyperwyc.Cabinet;
using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers the half of issue #49 that had already been demonstrated broken: the remedy the
/// library recommends for an unreadable store has to work on an unreadable store.
/// </summary>
/// <remarks>
/// <c>ResetAsync</c> used to enumerate records and remove them one at a time, which means
/// decrypting and deserialising first — exactly what a store worth resetting cannot do. So the
/// escape hatch was unavailable in the only situation that needs it, and the failure was real:
/// changing <c>CachedResponse.Body</c> from <c>string</c> to <c>byte[]</c> left a store on a
/// device that threw on every read and could not be cleared.
/// </remarks>
public sealed class CabinetResetTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"hyperwyc-reset-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Envelope Queued() =>
        new() { Url = "https://example.com/api/sales", Method = "POST" };

    [Fact]
    public async Task Reset_ClearsAStoreThatCanBeRead()
    {
        var store = new CabinetStore(_dir);
        await store.UpsertAsync(Queued());
        Assert.Single(await store.GetPendingOutboxAsync());

        await store.ResetAsync();

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task Reset_ClearsAStoreThatCannotBeDecrypted()
    {
        // Written under one key, opened under another — the shape of a moved store directory,
        // since the default key is derived from the path.
        var written = new CabinetStore(_dir, new byte[32]);
        await written.UpsertAsync(Queued());

        var otherKey = new byte[32];
        otherKey[0] = 0xFF;
        var unreadable = new CabinetStore(_dir, otherKey);

        await Assert.ThrowsAnyAsync<Exception>(() => unreadable.GetPendingOutboxAsync());

        // The remedy has to work here or it is not a remedy.
        await unreadable.ResetAsync();

        Assert.Empty(await unreadable.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task Reset_ClearsAStoreWhosePersistedShapeNoLongerDeserialises()
    {
        // The failure that actually happened: a store written by a previous build whose
        // Envelope shape has since changed.
        var store = new CabinetStore(_dir);
        await store.UpsertAsync(Queued());

        var records = Path.Combine(_dir, "records");
        foreach (var file in Directory.GetFiles(records))
            await File.WriteAllBytesAsync(file, [0x01, 0x02, 0x03, 0x04]);

        var reopened = new CabinetStore(_dir);
        await Assert.ThrowsAnyAsync<Exception>(() => reopened.GetPendingOutboxAsync());

        await reopened.ResetAsync();

        Assert.Empty(await reopened.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task Reset_LeavesTheStoreUsable()
    {
        var store = new CabinetStore(_dir);
        await store.UpsertAsync(Queued());
        await store.ResetAsync();

        await store.UpsertAsync(Queued());

        Assert.Single(await store.GetPendingOutboxAsync());
    }
}
