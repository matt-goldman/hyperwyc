using static Hyperwyc.Tests.TestHealthFactory;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;
using Hyperwyc.Tests.Fakes;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers issue #37: replays are sent through the application's own pipeline, with
/// only <see cref="HyperwycHandler"/> stepping aside.
/// </summary>
/// <remarks>
/// Before this, replays went through a bare transport. Since the handler queues a
/// request before downstream handlers have run, an offline write captured no
/// <c>Authorization</c> header and replayed without one — so offline writes did not
/// work against an authenticated API in either handler ordering.
/// </remarks>
public class ReplayPipelineTests
{
    private const string Url = "https://example.com/api/orders";

    /// <summary>Stands in for a consumer's auth handler.</summary>
    private sealed class TokenStampingHandler : DelegatingHandler
    {
        private readonly Func<string> _tokenFactory;

        public TokenStampingHandler(Func<string> tokenFactory) => _tokenFactory = tokenFactory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_tokenFactory()}");
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Terminal handler recording what actually reached the wire.</summary>
    private sealed class RecordingTransport : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(StatusCode));
        }
    }

    private static ServiceProvider BuildProvider(
        InMemoryStore store,
        RecordingTransport transport,
        FakeConnectivityService connectivity,
        Func<string>? tokenFactory = null,
        string clientName = "TestApi")
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService>(connectivity);

        var builder = services.AddHttpClient(clientName)
            .AddHyperwycHandler()
            .ConfigurePrimaryHttpMessageHandler(() => transport);

        if (tokenFactory is not null)
        {
            services.AddTransient(_ => new TokenStampingHandler(tokenFactory));
            builder.AddHttpMessageHandler<TokenStampingHandler>();
        }

        services.AddHyperwycCore(_ => store, o => o.FlushOnStartup = false);
        return services.BuildServiceProvider();
    }

    // -------------------------------------------------------------------------
    // The defect this issue exists for
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReplayedRequest_CarriesTokenAppliedAtReplayTime_NotQueueTime()
    {
        var store = new InMemoryStore();
        var transport = new RecordingTransport();
        var connectivity = new FakeConnectivityService(isConnected: false);
        var token = "stale-token";

        using var sp = BuildProvider(store, transport, connectivity, tokenFactory: () => token);
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("TestApi");

        // Queued while offline. The auth handler sits after Hyperwyc, so it never ran
        // and nothing was captured on the envelope.
        await client.PostAsync(Url, new StringContent("{}"));

        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.False(queued.RequestHeaders.ContainsKey("Authorization"));

        // Connectivity returns, and the token has rotated in the meantime.
        token = "fresh-token";
        connectivity.IsConnected = true;

        await sp.GetRequiredService<IHyperwyc>().FlushAsync();

        var replayed = Assert.Single(transport.Requests);
        Assert.Equal(
            "Bearer fresh-token",
            replayed.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task ReplayedRequest_ReachesHandlersRegisteredAfterHyperwyc()
    {
        var store = new InMemoryStore();
        var transport = new RecordingTransport();
        var connectivity = new FakeConnectivityService(isConnected: false);

        using var sp = BuildProvider(store, transport, connectivity, tokenFactory: () => "t");
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("TestApi");

        await client.PostAsync(Url, new StringContent("{}"));
        connectivity.IsConnected = true;
        await sp.GetRequiredService<IHyperwyc>().FlushAsync();

        Assert.Single(transport.Requests);
        Assert.True(transport.Requests[0].Headers.Contains("Authorization"));
    }

    // -------------------------------------------------------------------------
    // The handler steps aside rather than re-intercepting
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Replay_DoesNotPublishDuplicateDeliveredEvents()
    {
        var store = new InMemoryStore();
        var transport = new RecordingTransport();
        var connectivity = new FakeConnectivityService(isConnected: false);

        using var sp = BuildProvider(store, transport, connectivity);
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("TestApi");
        await client.PostAsync(Url, new StringContent("{}"));

        var hyperwyc = sp.GetRequiredService<IHyperwyc>();
        var synced = new List<HyperwycEvent>();
        using var subscription = hyperwyc.Events.Subscribe(new CollectingObserver(synced));

        connectivity.IsConnected = true;
        await hyperwyc.FlushAsync();

        // The orchestrator owns the outcome; the handler must not also report it.
        Assert.Single(synced, e => e.Type == HyperwycEventType.OnDelivered);
    }

    [Fact]
    public async Task Replay_IsNotQueuedAgainWhenConnectivityDropsMidFlush()
    {
        var store = new InMemoryStore();
        var transport = new RecordingTransport();

        // Still reporting offline while the flush runs: without the replay marker the
        // handler would queue the replay a second time instead of sending it.
        var connectivity = new FakeConnectivityService(isConnected: false);

        using var sp = BuildProvider(store, transport, connectivity);
        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("TestApi");
        await client.PostAsync(Url, new StringContent("{}"));

        await sp.GetRequiredService<IHyperwyc>().FlushAsync();

        Assert.Single(transport.Requests);
        Assert.Empty(await store.GetPendingOutboxAsync());
    }

    // -------------------------------------------------------------------------
    // Multiple clients
    // -------------------------------------------------------------------------

    [Fact]
    public async Task EachEnvelope_ReplaysThroughItsOwnClientPipeline()
    {
        var store = new InMemoryStore();
        var ordersTransport = new RecordingTransport();
        var profileTransport = new RecordingTransport();
        var connectivity = new FakeConnectivityService(isConnected: false);

        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService>(connectivity);

        services.AddHttpClient("OrdersApi")
            .AddHyperwycHandler()
            .ConfigurePrimaryHttpMessageHandler(() => ordersTransport);
        services.AddHttpClient("ProfileApi")
            .AddHyperwycHandler()
            .ConfigurePrimaryHttpMessageHandler(() => profileTransport);

        services.AddHyperwycCore(_ => store, o => o.FlushOnStartup = false);
        using var sp = services.BuildServiceProvider();

        var factory = sp.GetRequiredService<IHttpClientFactory>();
        await factory.CreateClient("OrdersApi").PostAsync("https://example.com/orders", new StringContent("{}"));
        await factory.CreateClient("ProfileApi").PostAsync("https://example.com/profile", new StringContent("{}"));

        connectivity.IsConnected = true;
        await sp.GetRequiredService<IHyperwyc>().FlushAsync();

        Assert.Single(ordersTransport.Requests);
        Assert.Equal("https://example.com/orders", ordersTransport.Requests[0].RequestUri?.ToString());
        Assert.Single(profileTransport.Requests);
        Assert.Equal("https://example.com/profile", profileTransport.Requests[0].RequestUri?.ToString());
    }

    [Fact]
    public async Task QueuedEnvelope_RecordsTheClientItCameFrom()
    {
        var store = new InMemoryStore();
        var connectivity = new FakeConnectivityService(isConnected: false);

        using var sp = BuildProvider(store, new RecordingTransport(), connectivity, clientName: "OrdersApi");
        await sp.GetRequiredService<IHttpClientFactory>()
            .CreateClient("OrdersApi")
            .PostAsync(Url, new StringContent("{}"));

        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal("OrdersApi", queued.ClientName);
    }

    // -------------------------------------------------------------------------
    // Typed clients
    // -------------------------------------------------------------------------

    /// <summary>A typed client, as most applications actually register them.</summary>
    private sealed class TypedApiClient(HttpClient http)
    {
        public Task<HttpResponseMessage> RecordAsync(string url) =>
            http.PostAsync(url, new StringContent("{}"));
    }

    [Fact]
    public async Task TypedClient_QueuesWithItsNameAndReplaysThroughItsPipeline()
    {
        var store = new InMemoryStore();
        var transport = new RecordingTransport();
        var connectivity = new FakeConnectivityService(isConnected: false);

        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService>(connectivity);
        services.AddTransient(_ => new TokenStampingHandler(() => "typed-token"));

        // AddHttpClient<T>() rather than AddHttpClient("name") — the registration style
        // the sample uses, and the one most applications reach for.
        services.AddHttpClient<TypedApiClient>()
            .AddHyperwycHandler()
            .AddHttpMessageHandler<TokenStampingHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => transport);

        services.AddHyperwycCore(_ => store, o => o.FlushOnStartup = false);
        using var sp = services.BuildServiceProvider();

        await sp.GetRequiredService<TypedApiClient>().RecordAsync(Url);

        // A typed client is a named client whose name is the type name, so the envelope
        // has something to replay through.
        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Equal(nameof(TypedApiClient), queued.ClientName);

        connectivity.IsConnected = true;
        await sp.GetRequiredService<IHyperwyc>().FlushAsync();

        var replayed = Assert.Single(transport.Requests);
        Assert.Equal("Bearer typed-token", replayed.Headers.GetValues("Authorization").Single());
    }

    // -------------------------------------------------------------------------
    // Fallback when no client name is available
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandlerRegisteredWithoutAName_FallsBackToReplayTransport()
    {
        var store = new InMemoryStore();
        var fallback = new RecordingTransport();
        var connectivity = new FakeConnectivityService(isConnected: false);

        var services = new ServiceCollection();
        services.AddSingleton<IConnectivityService>(connectivity);
        services.AddHyperwycCore(_ => store, o =>
        {
            o.FlushOnStartup = false;
            o.ReplayTransport = fallback;
        });
        using var sp = services.BuildServiceProvider();

        // Queued through a handler built without a client name, as the plain
        // AddHttpMessageHandler<HyperwycHandler>() registration would produce.
        var handler = sp.GetRequiredService<HyperwycHandler>();
        handler.InnerHandler = new RecordingTransport();
        using (var client = new HttpClient(handler))
            await client.PostAsync(Url, new StringContent("{}"));

        var queued = Assert.Single(await store.GetPendingOutboxAsync());
        Assert.Null(queued.ClientName);

        connectivity.IsConnected = true;
        await sp.GetRequiredService<IHyperwyc>().FlushAsync();

        Assert.Single(fallback.Requests);
    }

    private sealed class CollectingObserver(List<HyperwycEvent> collected) : IObserver<HyperwycEvent>
    {
        public void OnNext(HyperwycEvent value) => collected.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
