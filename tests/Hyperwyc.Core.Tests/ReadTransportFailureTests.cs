using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using System.Text;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// A read whose transport cannot answer gets the same response as a read made while the
/// connectivity service reports offline. The two are the same situation, so a caller must not be
/// able to tell them apart.
/// </summary>
/// <remarks>
/// This is the read half of the asymmetry fixed for writes: previously the fallback ran only
/// under <c>NetworkFirst</c>, and even then rethrew once the cached copy was past its TTL — so a
/// wrong connectivity answer turned a clean "no data" into an exception.
/// </remarks>
public class ReadTransportFailureTests
{
    private static HttpClient Client(
        InMemoryStore store, bool connected, HttpMessageHandler transport, HyperwycOptions? options = null) =>
        new(new HyperwycHandler(
            store,
            new FakeConnectivityService(isConnected: connected),
            new HyperwycEventStream(),
            options ?? new HyperwycOptions(),
            TestHealth())
        {
            InnerHandler = transport,
        });

    private static StubHttpMessageHandler Dead(HttpRequestError error = HttpRequestError.ConnectionError) =>
        new((Func<HttpRequestMessage, HttpResponseMessage>)(
            _ => throw new HttpRequestException(error, "the transport could not answer")));

    private static CachedResponse Cached(string url, string body, TimeSpan age) =>
        new()
        {
            Url = url,
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(body),
            CachedAt = DateTimeOffset.UtcNow - age,
        };

    private const string Url = "https://example.com/api/items";

    [Theory]
    [InlineData(SourcePriority.CacheFirst)]
    [InlineData(SourcePriority.NetworkFirst)]
    public async Task NoCache_AnswersOffline_RatherThanThrowing(SourcePriority priority)
    {
        var options = new HyperwycOptions();
        options.Routes.For("*", new RoutePolicy { SourcePriority = priority });

        var store = new InMemoryStore();
        using var client = Client(store, connected: true, Dead(), options);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(SourcePriority.CacheFirst)]
    [InlineData(SourcePriority.NetworkFirst)]
    public async Task StaleCache_AnswersOffline_RatherThanThrowing(SourcePriority priority)
    {
        // Past its TTL the stored copy is not servable — that is the validity bound, and it
        // means the same thing here as it does offline. What must not happen is an exception.
        var options = new HyperwycOptions();
        options.Routes.For("*", new RoutePolicy { SourcePriority = priority, Ttl = TimeSpan.FromMinutes(5) });

        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached(Url, """{"v":1}""", age: TimeSpan.FromHours(2)));
        using var client = Client(store, connected: true, Dead(), options);

        var response = await client.GetAsync(Url);

        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
    }

    [Theory]
    [InlineData(SourcePriority.CacheFirst)]
    [InlineData(SourcePriority.NetworkFirst)]
    public async Task ValidCache_IsServed(SourcePriority priority)
    {
        var options = new HyperwycOptions();
        options.Routes.For("*", new RoutePolicy { SourcePriority = priority, Ttl = TimeSpan.FromHours(1) });

        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached(Url, """{"v":1}""", age: TimeSpan.FromMinutes(1)));
        using var client = Client(store, connected: true, Dead(), options);

        var response = await client.GetAsync(Url);

        Assert.Equal("""{"v":1}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheAnswerIsIdenticalToBeingOffline()
    {
        // The point of the whole change: a caller cannot tell whether the connectivity service
        // was right, so being wrong cannot cost correctness.
        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached(Url, """{"v":1}""", age: TimeSpan.FromDays(9)));

        using var wrong = Client(store, connected: true, Dead());
        using var right = Client(store, connected: false, Dead());

        var a = await wrong.GetAsync(Url);
        var b = await right.GetAsync(Url);

        Assert.Equal(b.StatusCode, a.StatusCode);
        Assert.Equal(
            b.Headers.GetValues("X-Hyperwyc-Status").Single(),
            a.Headers.GetValues("X-Hyperwyc-Status").Single());
        Assert.Equal(await b.Content.ReadAsStringAsync(), await a.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NetworkOnlyRoute_StillAnswersOffline_WithoutTouchingTheStore()
    {
        var options = new HyperwycOptions();
        options.Routes.For("*", RoutePolicy.NetworkOnly());

        var store = new InMemoryStore();
        await store.PutCachedResponseAsync(Cached(Url, """{"v":1}""", age: TimeSpan.Zero));
        using var client = Client(store, connected: true, Dead(), options);

        // NetworkOnly opts out of the store, so there is nothing to serve — but the caller is
        // still given an answer rather than an exception, exactly as when offline.
        var response = await client.GetAsync(Url);

        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
    }
}
