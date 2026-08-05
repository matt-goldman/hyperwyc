using System.Net;
using System.Text;
using System.Text.Json;
using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

public class EnvelopeTests
{
    // -------------------------------------------------------------------------
    // ForRequest
    // -------------------------------------------------------------------------

    [Fact]
    public void ForRequest_MapsUrlAndMethod()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/orders");

        var envelope = Envelope.ForRequest(request);

        Assert.Equal("https://example.com/api/orders", envelope.Url);
        Assert.Equal("POST", envelope.Method);
    }

    [Fact]
    public void ForRequest_AssignsValidGuid()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/items");

        var envelope = Envelope.ForRequest(request);

        Assert.True(Guid.TryParse(envelope.Id, out _));
    }

    [Fact]
    public void ForRequest_EachEnvelopeGetsUniqueId()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/items");

        var id1 = Envelope.ForRequest(request).Id;
        var id2 = Envelope.ForRequest(request).Id;

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ForRequest_MapsRequestHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/items");
        request.Headers.Add("X-Tenant", "acme");

        var envelope = Envelope.ForRequest(request);

        Assert.True(envelope.RequestHeaders.ContainsKey("X-Tenant"));
        Assert.Equal("acme", envelope.RequestHeaders["X-Tenant"]);
    }

    [Fact]
    public void ForRequest_MapsRequestBody()
    {
        const string json = """{"name":"widget"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/items")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var envelope = Envelope.ForRequest(request);

        Assert.Equal(json, envelope.RequestBody);
    }

    [Fact]
    public void ForRequest_NullBodyRequest_HasNullRequestBody()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/items");

        var envelope = Envelope.ForRequest(request);

        Assert.Null(envelope.RequestBody);
    }

    [Fact]
    public void ForRequest_DefaultsSyncFlags()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/items");

        var envelope = Envelope.ForRequest(request);

        Assert.False(envelope.IsSynced);
        Assert.False(envelope.IsDeadLettered);
        Assert.Equal(0, envelope.RetryCount);
        Assert.Null(envelope.NextRetryUtc);
        Assert.Null(envelope.Response);
    }

    [Fact]
    public void ForRequest_ThrowsOnNullRequest()
    {
        Assert.Throws<ArgumentNullException>(() => Envelope.ForRequest(null!));
    }

    [Fact]
    public void ForRequest_RoundTripsJson()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/orders")
        {
            Content = new StringContent("""{"qty":3}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Correlation-Id", "abc-123");

        var envelope = Envelope.ForRequest(request);

        var json = JsonSerializer.Serialize(envelope);
        var restored = JsonSerializer.Deserialize<Envelope>(json);

        Assert.NotNull(restored);
        Assert.Equal(envelope.Id, restored.Id);
        Assert.Equal(envelope.Url, restored.Url);
        Assert.Equal(envelope.Method, restored.Method);
        Assert.Equal(envelope.RequestBody, restored.RequestBody);
        Assert.Equal(envelope.RequestHeaders["X-Correlation-Id"], restored.RequestHeaders["X-Correlation-Id"]);
        Assert.Equal(envelope.IsSynced, restored.IsSynced);
        Assert.Equal(envelope.CreatedUtc, restored.CreatedUtc);
    }

    // -------------------------------------------------------------------------
    // ForCachedResponse
    // -------------------------------------------------------------------------

    [Fact]
    public void ForCachedResponse_SetsIsSyncedTrue()
    {
        var (request, response) = BuildGetPair();

        var envelope = Envelope.ForCachedResponse(request, response);

        Assert.True(envelope.IsSynced);
    }

    [Fact]
    public void ForCachedResponse_PopulatesResponse()
    {
        var (request, response) = BuildGetPair(statusCode: HttpStatusCode.OK, body: """{"id":1}""");

        var envelope = Envelope.ForCachedResponse(request, response);

        Assert.NotNull(envelope.Response);
        Assert.Equal(200, envelope.Response.StatusCode);
        Assert.Equal("""{"id":1}""", envelope.Response.Body);
        Assert.True(DateTimeOffset.UtcNow - envelope.Response.CachedAt < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ForCachedResponse_MapsResponseHeaders()
    {
        var (request, response) = BuildGetPair();
        response.Headers.Add("X-Request-Id", "req-456");

        var envelope = Envelope.ForCachedResponse(request, response);

        Assert.Contains(
            envelope.Response!.Headers,
            h => h.Key.Equals("X-Request-Id", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("req-456", envelope.Response.Headers["X-Request-Id"]);

    }

    [Fact]
    public void ForCachedResponse_StillMapsRequestFields()
    {
        var (request, response) = BuildGetPair();

        var envelope = Envelope.ForCachedResponse(request, response);

        Assert.Equal("https://example.com/items", envelope.Url);
        Assert.Equal("GET", envelope.Method);
    }

    [Fact]
    public void ForCachedResponse_ThrowsOnNullRequest()
    {
        var (_, response) = BuildGetPair();
        Assert.Throws<ArgumentNullException>(() => Envelope.ForCachedResponse(null!, response));
    }

    [Fact]
    public void ForCachedResponse_ThrowsOnNullResponse()
    {
        var (request, _) = BuildGetPair();
        Assert.Throws<ArgumentNullException>(() => Envelope.ForCachedResponse(request, null!));
    }

    [Fact]
    public void ForCachedResponse_RoundTripsJson()
    {
        var (request, response) = BuildGetPair(statusCode: HttpStatusCode.OK, body: """{"id":42}""");
        response.Headers.Add("ETag", "\"v1\"");

        var envelope = Envelope.ForCachedResponse(request, response);

        var json = JsonSerializer.Serialize(envelope);
        var restored = JsonSerializer.Deserialize<Envelope>(json);

        Assert.NotNull(restored);
        Assert.Equal(envelope.Id, restored.Id);
        Assert.Equal(envelope.Url, restored.Url);
        Assert.True(restored.IsSynced);
        Assert.NotNull(restored.Response);
        Assert.Equal(envelope.Response!.StatusCode, restored.Response.StatusCode);
        Assert.Equal(envelope.Response.Body, restored.Response.Body);
        Assert.Equal(envelope.Response.CachedAt, restored.Response.CachedAt);
        Assert.Equal(envelope.Response.Headers["ETag"], restored.Response.Headers["ETag"]);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (HttpRequestMessage Request, HttpResponseMessage Response) BuildGetPair(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/items");
        var response = new HttpResponseMessage(statusCode)
        {
            Content = body is not null
                ? new StringContent(body, Encoding.UTF8, "application/json")
                : null,
        };
        return (request, response);
    }
}
