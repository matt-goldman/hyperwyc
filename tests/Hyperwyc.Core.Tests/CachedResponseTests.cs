using System.Net;
using System.Text;
using System.Text.Json;
using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

public class CachedResponseTests
{
    private const string Url = "https://example.com/items";

    [Fact]
    public void For_KeepsTheUrlItWasFetchedFrom()
    {
        var response = Ok("""{"id":1}""");

        var cached = CachedResponse.For(Url, response);

        Assert.Equal(Url, cached.Url);
    }

    [Fact]
    public void For_CapturesStatusBodyAndTime()
    {
        var response = Ok("""{"id":1}""");

        var cached = CachedResponse.For(Url, response);

        Assert.Equal(200, cached.StatusCode);
        Assert.Equal("""{"id":1}""", cached.GetBodyAsText());
        Assert.True(DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void For_MapsResponseHeaders()
    {
        var response = Ok();
        response.Headers.Add("X-Request-Id", "req-456");

        var cached = CachedResponse.For(Url, response);

        Assert.Contains(
            cached.Headers,
            h => h.Key.Equals("X-Request-Id", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("req-456", cached.Headers["X-Request-Id"]);
    }

    [Fact]
    public void For_MapsContentHeaders()
    {
        // The media type lives on the content headers, and it is the only thing that keeps a
        // cached image an image once the body is bytes. See issue #25.
        var cached = CachedResponse.For(Url, Ok("""{"id":1}"""));

        Assert.Equal("application/json; charset=utf-8", cached.Headers["Content-Type"]);
    }

    [Fact]
    public void For_ThrowsOnNullUrl()
    {
        Assert.Throws<ArgumentNullException>(() => CachedResponse.For(null!, Ok()));
    }

    [Fact]
    public void For_ThrowsOnNullResponse()
    {
        Assert.Throws<ArgumentNullException>(() => CachedResponse.For(Url, null!));
    }

    [Fact]
    public void For_RoundTripsJson()
    {
        var response = Ok("""{"id":42}""");
        response.Headers.Add("ETag", "\"v1\"");

        var cached = CachedResponse.For(Url, response);

        var json = JsonSerializer.Serialize(cached);
        var restored = JsonSerializer.Deserialize<CachedResponse>(json);

        Assert.NotNull(restored);
        Assert.Equal(cached.Url, restored.Url);
        Assert.Equal(cached.StatusCode, restored.StatusCode);
        Assert.Equal(cached.Body, restored.Body);
        Assert.Equal(cached.CachedAt, restored.CachedAt);
        Assert.Equal(cached.Headers["ETag"], restored.Headers["ETag"]);
    }

    private static HttpResponseMessage Ok(string? body = null) =>
        new(HttpStatusCode.OK)
        {
            Content = body is not null
                ? new StringContent(body, Encoding.UTF8, "application/json")
                : null,
        };
}
