using System.Net;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Verifies how <see cref="HyperwycHandler"/> participates in an
/// <see cref="HttpClient"/> pipeline: when offline it short-circuits the
/// pipeline (no handler chained after it runs); when online it delegates
/// normally so downstream handlers (e.g. an auth handler) can mutate the
/// outgoing request.
/// </summary>
public class HyperwycHandlerPipelineTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// A minimal downstream <see cref="DelegatingHandler"/> that stands in for
    /// an auth (or any other) handler placed after <see cref="HyperwycHandler"/>
    /// in the pipeline. Tracks how many times it was invoked and adds a header
    /// to outgoing requests so the test can prove the mutation reached the
    /// transport.
    /// </summary>
    private sealed class HeaderInjectingHandler : DelegatingHandler
    {
        private readonly string _name;
        private readonly string _value;

        public int CallCount { get; private set; }

        public HeaderInjectingHandler(string name, string value, HttpMessageHandler inner)
        {
            _name = name;
            _value = value;
            InnerHandler = inner;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            request.Headers.TryAddWithoutValidation(_name, _value);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static (HttpClient Client, HeaderInjectingHandler Downstream, StubHttpMessageHandler Transport)
        BuildPipeline(bool isConnected, InMemorySyncStore? store = null)
    {
        var transport = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var downstream = new HeaderInjectingHandler("X-Test-Auth", "token-123", transport);

        var hyperwyc = new HyperwycHandler(
            store ?? new InMemorySyncStore(),
            new FakeConnectivityService(isConnected),
            new FakeSyncPolicy(),
            new SyncEventStream(),
            new HyperwycOptions())
        {
            InnerHandler = downstream,
        };

        return (new HttpClient(hyperwyc), downstream, transport);
    }

    // -------------------------------------------------------------------------
    // Offline: pipeline must be short-circuited
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OfflineWrite_ShortCircuitsDownstreamHandlers()
    {
        var (client, downstream, transport) = BuildPipeline(isConnected: false);
        using (client)
        {
            var response = await client.PostAsync("https://example.com/api/orders", content: null);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Assert.Equal(0, downstream.CallCount);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task OfflineRead_NoCache_ShortCircuitsDownstreamHandlers()
    {
        var (client, downstream, transport) = BuildPipeline(isConnected: false);
        using (client)
        {
            var response = await client.GetAsync("https://example.com/api/items");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.TryGetValues(
                HyperwycResponseFactory.StatusHeader, out var values));
            Assert.Equal("Offline", values!.First());
        }

        Assert.Equal(0, downstream.CallCount);
        Assert.Equal(0, transport.CallCount);
    }

    // -------------------------------------------------------------------------
    // Online: downstream handlers must run and see the request
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnlineRequest_DownstreamHandlerRunsAndCanMutateRequest()
    {
        var (client, downstream, transport) = BuildPipeline(isConnected: true);
        using (client)
        {
            await client.GetAsync("https://example.com/api/items");
        }

        Assert.Equal(1, downstream.CallCount);
        Assert.Equal(1, transport.CallCount);

        // Prove the downstream handler's header reached the transport — this is
        // the contract that lets users place an auth handler after Hyperwyc and
        // have replayed requests pick up a freshly minted token.
        Assert.True(transport.LastRequest!.Headers.TryGetValues("X-Test-Auth", out var values));
        Assert.Equal("token-123", values!.First());
    }
}
