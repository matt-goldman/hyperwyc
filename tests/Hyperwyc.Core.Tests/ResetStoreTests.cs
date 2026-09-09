using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue #16: <see cref="IHyperwyc.ResetStoreAsync"/> discards cached responses and
/// queued writes — typically on logout — without racing an in-flight flush.
/// </summary>
public class ResetStoreTests
{
    private const string Url = "https://example.com/api/sales";

    /// <summary>
    /// Holds the first request until released, so a flush can be observed mid-loop rather
    /// than inferred from timing.
    /// </summary>
    private sealed class GatedTransport(HttpStatusCode status) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public int CallCount;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return new HttpResponseMessage(status);
        }
    }

    // -------------------------------------------------------------------------
    // The basics
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Reset_EmptyStore_Succeeds()
    {
        var store = new InMemoryStore();
        using var sp = Build(store, new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)));

        await sp.GetRequiredService<IHyperwyc>().ResetStoreAsync();

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task Reset_DiscardsQueuedWritesAndCachedResponses()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        await store.UpsertAsync(Cached("https://example.com/api/products"));

        using var sp = Build(store, new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)));

        await sp.GetRequiredService<IHyperwyc>().ResetStoreAsync();

        Assert.Empty(await store.GetPendingOutboxAsync());
        Assert.Null(await store.GetCachedResponseAsync("https://example.com/api/products"));
    }

    [Fact]
    public async Task Reset_LeavesTheStoreUsable()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var sp = Build(store, transport);
        var hyperwyc = sp.GetRequiredService<IHyperwyc>();

        await hyperwyc.ResetStoreAsync();
        await store.UpsertAsync(Outbox());
        await hyperwyc.FlushAsync();

        // The envelope queued after the reset is delivered normally.
        Assert.Equal(1, transport.CallCount);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    // -------------------------------------------------------------------------
    // Reset does not flush
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Reset_DoesNotSendQueuedWrites()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var sp = Build(store, transport);

        await sp.GetRequiredService<IHyperwyc>().ResetStoreAsync();

        // Reset means discard. Delivering on the way out would, on the logout this exists
        // for, replay through an auth handler whose credentials the app is in the middle of
        // revoking — every write 401s, counts as delivered, and is wiped anyway.
        Assert.Equal(0, transport.CallCount);
    }

    // -------------------------------------------------------------------------
    // The race the acceptance criterion is about
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Reset_WaitsForAnInFlightFlush()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());

        var transport = new GatedTransport(HttpStatusCode.OK);
        using var sp = Build(store, transport);
        var hyperwyc = sp.GetRequiredService<IHyperwyc>();

        var flush = hyperwyc.FlushAsync();
        await transport.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var reset = hyperwyc.ResetStoreAsync();

        // Blocked on the flush gate. FlushAsync's own try-acquire returns immediately when a
        // flush is running, so delegating to it would not have waited at all.
        var finishedEarly = await Task.WhenAny(reset, Task.Delay(200));
        Assert.NotSame(reset, finishedEarly);

        transport.Release();
        await Task.WhenAll(flush, reset).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task Reset_IsNotUndoneByAFlushWritingBackAfterTheWipe()
    {
        // The sharp end. A transiently-failing envelope is upserted by DeferAsync when the
        // attempt completes; if that lands after the wipe the envelope is resurrected, and on
        // a logout that means the previous user's queued write comes back.
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());

        var transport = new GatedTransport(HttpStatusCode.ServiceUnavailable);

        using var sp = Build(store, transport);
        var hyperwyc = sp.GetRequiredService<IHyperwyc>();

        var flush = hyperwyc.FlushAsync();
        await transport.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var reset = hyperwyc.ResetStoreAsync();
        transport.Release();
        await Task.WhenAll(flush, reset).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task Reset_ThenFlush_SendsNothing()
    {
        var store = new InMemoryStore();
        await store.UpsertAsync(Outbox());
        await store.UpsertAsync(Outbox());

        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        using var sp = Build(store, transport);
        var hyperwyc = sp.GetRequiredService<IHyperwyc>();

        await hyperwyc.ResetStoreAsync();
        await hyperwyc.FlushAsync();

        Assert.Equal(0, transport.CallCount);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Envelope Outbox() => new() { Url = Url, Method = "POST" };

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

    private static ServiceProvider Build(IHyperwycStore store, HttpMessageHandler transport)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService>(new FakeConnectivityService(isConnected: true));
        services.AddHyperwycCore(_ => store, o =>
        {
            o.FlushOnStartup = false;
            o.ReplayTransport = transport;
        });
        return services.BuildServiceProvider();
    }
}
