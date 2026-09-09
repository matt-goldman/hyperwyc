using System.Text;
using System.Text.Json;
using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

public class QueuedWriteTests
{
    [Fact]
    public void For_MapsUrlAndMethod()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/orders");

        var write = QueuedWrite.For(request);

        Assert.Equal("https://example.com/api/orders", write.Url);
        Assert.Equal("POST", write.Method);
    }

    [Fact]
    public void For_AssignsValidGuid()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/items");

        var write = QueuedWrite.For(request);

        Assert.True(Guid.TryParse(write.Id, out _));
    }

    [Fact]
    public void For_EachWriteGetsUniqueId()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/items");

        var id1 = QueuedWrite.For(request).Id;
        var id2 = QueuedWrite.For(request).Id;

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void For_MapsRequestHeaders()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/items");
        request.Headers.Add("X-Tenant", "acme");

        var write = QueuedWrite.For(request);

        Assert.True(write.RequestHeaders.ContainsKey("X-Tenant"));
        Assert.Equal("acme", write.RequestHeaders["X-Tenant"]);
    }

    [Fact]
    public void For_MapsRequestBody()
    {
        const string json = """{"name":"widget"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/items")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        var write = QueuedWrite.For(request);

        Assert.Equal(json, write.GetRequestBodyAsText());
    }

    [Fact]
    public void For_NullBodyRequest_HasNullRequestBody()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "https://example.com/items/1");

        var write = QueuedWrite.For(request);

        Assert.Null(write.RequestBody);
    }

    [Fact]
    public void For_HasNoOutcomeUntilOneIsAttempted()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/items");

        var write = QueuedWrite.For(request);

        Assert.Null(write.LastOutcome);
    }

    [Fact]
    public void For_ThrowsOnNullRequest()
    {
        Assert.Throws<ArgumentNullException>(() => QueuedWrite.For(null!));
    }

    [Fact]
    public void For_RoundTripsJson()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/orders")
        {
            Content = new StringContent("""{"qty":3}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Correlation-Id", "abc-123");

        var write = QueuedWrite.For(request);

        var json = JsonSerializer.Serialize(write);
        var restored = JsonSerializer.Deserialize<QueuedWrite>(json);

        Assert.NotNull(restored);
        Assert.Equal(write.Id, restored.Id);
        Assert.Equal(write.CorrelationId, restored.CorrelationId);
        Assert.Equal(write.Url, restored.Url);
        Assert.Equal(write.Method, restored.Method);
        Assert.Equal(write.RequestBody, restored.RequestBody);
        Assert.Equal(write.RequestHeaders["X-Correlation-Id"], restored.RequestHeaders["X-Correlation-Id"]);
        Assert.Equal(write.CreatedUtc, restored.CreatedUtc);
    }
}
