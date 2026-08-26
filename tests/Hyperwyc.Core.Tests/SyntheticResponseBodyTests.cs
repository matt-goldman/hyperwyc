using System.Net;
using System.Net.Http.Json;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Synthetic responses carry the JSON <c>null</c> literal rather than an empty body.
/// </summary>
/// <remarks>
/// An empty body is not "no data" to a JSON deserialiser — it is not JSON, and
/// <c>GetFromJsonAsync&lt;T&gt;</c> throws on it for a single object just as much as for a
/// collection. These tests exercise the real extension methods rather than asserting on the
/// body string, because the thing worth protecting is that a caller's ordinary
/// deserialisation does not blow up when Hyperwyc has nothing to give it.
/// </remarks>
public class SyntheticResponseBodyTests
{
    private sealed record Product(int Id, string? Name);

    private static HyperwycHandler BuildHandler(
        InMemorySyncStore? store = null,
        bool isConnected = false,
        CacheStrategy strategy = CacheStrategy.CacheFirst) =>
        new(
            store ?? new InMemorySyncStore(),
            new FakeConnectivityService(isConnected),
            new FakeSyncPolicy(strategy),
            new SyncEventStream(),
            new HyperwycOptions())
        { InnerHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)) };

    // -------------------------------------------------------------------------
    // The defect: a caller's ordinary deserialisation used to throw
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OfflineRead_NoCache_DeserialisesToNullObjectRatherThanThrowing()
    {
        using var client = new HttpClient(BuildHandler());

        var product = await client.GetFromJsonAsync<Product>("https://example.com/api/products/1");

        Assert.Null(product);
    }

    [Fact]
    public async Task OfflineRead_NoCache_DeserialisesToNullCollectionRatherThanThrowing()
    {
        using var client = new HttpClient(BuildHandler());

        var products = await client.GetFromJsonAsync<List<Product>>("https://example.com/api/products");

        // Null rather than empty: returning [] needs per-route knowledge Hyperwyc does not
        // have. The point here is that it no longer throws.
        Assert.Null(products);
    }

    [Fact]
    public async Task QueuedWrite_DeserialisesToNullRatherThanThrowing()
    {
        var store = new InMemorySyncStore();
        using var client = new HttpClient(BuildHandler(store));

        var response = await client.PostAsJsonAsync(
            "https://example.com/api/sales", new Product(1, "Bucket Tooth"));

        // A caller reading back the created resource gets null — there is no created
        // resource yet, which is the honest answer.
        var created = await response.Content.ReadFromJsonAsync<Product>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(created);
        Assert.Single(await store.GetPendingOutboxAsync());
    }

    [Fact]
    public async Task CacheOnlyMiss_DeserialisesToNullRatherThanThrowing()
    {
        using var client = new HttpClient(
            BuildHandler(isConnected: true, strategy: CacheStrategy.CacheOnly));

        var product = await client.GetFromJsonAsync<Product>("https://example.com/api/products/1");

        Assert.Null(product);
    }

    // -------------------------------------------------------------------------
    // Shape of the synthetic response
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SyntheticResponse_BodyIsTheJsonNullLiteral()
    {
        using var client = new HttpClient(BuildHandler());

        var response = await client.GetAsync("https://example.com/api/products");

        Assert.Equal("null", await response.Content.ReadAsStringAsync());

        // No media type is asserted. The bytes happen to be valid JSON, but Hyperwyc does
        // not know what the route serves, and GetFromJsonAsync does not require one.
        Assert.Null(response.Content.Headers.ContentType);
    }

    [Fact]
    public async Task SyntheticResponse_StillCarriesTheStatusHeader()
    {
        using var client = new HttpClient(BuildHandler());

        var response = await client.GetAsync("https://example.com/api/products");

        Assert.Equal("Offline", response.Headers.GetValues("X-Hyperwyc-Status").Single());
    }


    [Fact]
    public async Task CachedResponse_IsUnaffected()
    {
        var store = new InMemorySyncStore();
        var envelope = new Envelope { Url = "https://example.com/api/products", Method = "GET" };
        envelope.Response = new CachedResponse
        {
            StatusCode = 200,
            Body = """[{"id":1,"name":"Bucket Tooth"}]"""u8.ToArray(),
            CachedAt = DateTimeOffset.UtcNow,
        };
        envelope.IsSynced = true;
        await store.UpsertAsync(envelope);

        using var client = new HttpClient(BuildHandler(store));

        var products = await client.GetFromJsonAsync<List<Product>>("https://example.com/api/products");

        Assert.NotNull(products);
        Assert.Equal("Bucket Tooth", Assert.Single(products!).Name);
    }
}
