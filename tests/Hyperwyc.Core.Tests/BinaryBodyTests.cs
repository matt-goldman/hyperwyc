using static Hyperwyc.Tests.TestHealthFactory;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue #25: request and response bodies survive queueing and caching byte for byte.
/// </summary>
/// <remarks>
/// Bodies used to round-trip through <c>ReadAsStringAsync</c>, which decodes as UTF-8 and
/// re-encodes on the way out. That is lossless only for text that happens to be valid UTF-8.
/// Anything else — a PNG, protobuf, a gzip-encoded payload — came back mangled, and silently:
/// the request still went out, the response still deserialised into something, and only the
/// bytes were wrong.
/// </remarks>
public class BinaryBodyTests
{
    private const string Url = "https://example.com/api/assets";

    /// <summary>
    /// A PNG header plus bytes that are not valid UTF-8 — 0x80–0x8F are continuation bytes with
    /// no lead, so a UTF-8 decode replaces them with U+FFFD and the original is unrecoverable.
    /// </summary>
    private static byte[] PngBytes() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0xFF, 0xFE, 0xFD, 0xFC, 0x00, 0x01, 0x02, 0x03,
    ];

    private static byte[] Gzip(string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            var raw = Encoding.UTF8.GetBytes(content);
            gzip.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    // -------------------------------------------------------------------------
    // The corruption this issue exists for
    // -------------------------------------------------------------------------

    [Fact]
    public void PngBytes_AreNotUtf8RoundTrippable()
    {
        // Pins the premise. If this ever passes, the other tests here prove less than they look.
        var original = PngBytes();
        var mangled = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original));

        Assert.NotEqual(original, mangled);
    }

    // -------------------------------------------------------------------------
    // Requests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task QueuedBinaryRequest_IsCapturedByteForByte()
    {
        var store = new InMemoryStore();
        var payload = PngBytes();

        using var client = new HttpClient(OfflineHandler(store));
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        await client.PostAsync(Url, content);

        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal(payload, queued.RequestBody);
    }

    [Fact]
    public async Task ReplayedBinaryRequest_ArrivesByteForByte()
    {
        var store = new InMemoryStore();
        var payload = PngBytes();

        using (var client = new HttpClient(OfflineHandler(store)))
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            await client.PostAsync(Url, content);
        }

        var transport = new CapturingTransport();
        await using var orchestrator = Orchestrator(store, transport);
        await orchestrator.FlushAsync();

        var replayed = Assert.Single(transport.Requests);
        Assert.Equal(payload, replayed.Body);
        Assert.Equal("image/png", replayed.ContentType);
    }

    [Fact]
    public async Task ReplayedJsonRequest_IsStillExact()
    {
        const string Json = """{"quantity":60,"note":"café — naïve ☕"}""";

        var store = new InMemoryStore();
        using (var client = new HttpClient(OfflineHandler(store)))
        {
            await client.PostAsync(Url, new StringContent(Json, Encoding.UTF8, "application/json"));
        }

        var transport = new CapturingTransport();
        await using var orchestrator = Orchestrator(store, transport);
        await orchestrator.FlushAsync();

        var replayed = Assert.Single(transport.Requests);
        Assert.Equal(Json, Encoding.UTF8.GetString(replayed.Body!));
        Assert.Equal("application/json", replayed.ContentType);
    }

    // -------------------------------------------------------------------------
    // Responses
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CachedBinaryResponse_IsServedByteForByte()
    {
        var store = new InMemoryStore();
        var payload = PngBytes();

        var networkContent = new ByteArrayContent(payload);
        networkContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var stub = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = networkContent });

        // Online once to populate the cache.
        using (var online = new HttpClient(OnlineHandler(store, stub)))
            await online.GetAsync(Url);

        // Offline: the cached copy is the only answer available.
        using var offline = new HttpClient(OfflineHandler(store));
        var response = await offline.GetAsync(Url);
        var served = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(payload, served);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CachedGzipResponse_KeepsItsBytesAndContentEncoding()
    {
        var store = new InMemoryStore();
        var compressed = Gzip("""{"products":[{"id":1}]}""");

        var networkContent = new ByteArrayContent(compressed);
        networkContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        networkContent.Headers.ContentEncoding.Add("gzip");
        var stub = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = networkContent });

        using (var online = new HttpClient(OnlineHandler(store, stub)))
            await online.GetAsync(Url);

        using var offline = new HttpClient(OfflineHandler(store));
        var response = await offline.GetAsync(Url);
        var served = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(compressed, served);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);

        // And it still decompresses, which is the point of keeping the bytes intact.
        using var decompressed = new MemoryStream();
        using (var gzip = new GZipStream(new MemoryStream(served), CompressionMode.Decompress))
            gzip.CopyTo(decompressed);
        Assert.Equal("""{"products":[{"id":1}]}""", Encoding.UTF8.GetString(decompressed.ToArray()));
    }

    [Fact]
    public async Task CachedJsonResponse_IsStillExact()
    {
        const string Json = """{"name":"café — naïve ☕"}""";

        var store = new InMemoryStore();
        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Json, Encoding.UTF8, "application/json"),
        });

        using (var online = new HttpClient(OnlineHandler(store, stub)))
            await online.GetAsync(Url);

        using var offline = new HttpClient(OfflineHandler(store));
        var response = await offline.GetAsync(Url);

        Assert.Equal(Json, await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    // -------------------------------------------------------------------------
    // The cap still applies, and now measures the real thing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OversizedBinaryResponse_IsNotCached()
    {
        var store = new InMemoryStore();
        var big = new byte[4096];
        Random.Shared.NextBytes(big);

        var stub = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(big),
        });

        var handler = new HyperwycHandler(
            store,
            new FakeConnectivityService(isConnected: true),
            new HyperwycEventStream(),
            new HyperwycOptions { MaxCachedResponseBodyBytes = 1024 }, TestHealth())
        { InnerHandler = stub };

        using var client = new HttpClient(handler);
        await client.GetAsync(Url);

        Assert.Null(await store.GetCachedResponseAsync(Url));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static HyperwycHandler OfflineHandler(InMemoryStore store) =>
        new(store,
            new FakeConnectivityService(isConnected: false),
            new HyperwycEventStream(),
            new HyperwycOptions(), TestHealth())
        { InnerHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)) };

    private static HyperwycHandler OnlineHandler(InMemoryStore store, HttpMessageHandler inner) =>
        new(store,
            new FakeConnectivityService(isConnected: true),
            new HyperwycEventStream(),
            new HyperwycOptions(), TestHealth())
        { InnerHandler = inner };

    private static OutboxProcessor Orchestrator(InMemoryStore store, HttpMessageHandler transport) =>
        new(store,
            new FakeConnectivityService(isConnected: true),
            new HyperwycEventStream(),
            new HyperwycOptions(),
            transport, TestHealth());

    /// <summary>Reads what reached the wire before the request message is disposed.</summary>
    private sealed class CapturingTransport : HttpMessageHandler
    {
        public List<(byte[]? Body, string? ContentType)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            Requests.Add((body, request.Content?.Headers.ContentType?.MediaType));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
