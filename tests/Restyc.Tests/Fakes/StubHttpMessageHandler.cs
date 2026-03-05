namespace Restyc.Tests.Fakes;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that returns a pre-configured response
/// without making any real network request. Tracks how many times it was called.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

    public int CallCount { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>Returns <paramref name="response"/> for every request.</summary>
    public StubHttpMessageHandler(HttpResponseMessage response)
        : this(_ => response) { }

    /// <summary>Delegates each call to <paramref name="factory"/>.</summary>
    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        => _factory = factory;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        CallCount++;
        return Task.FromResult(_factory(request));
    }
}
