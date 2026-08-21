using Hyperwyc.Interfaces;
using Xunit;

namespace Hyperwyc.Tests;

/// <summary>
/// Covers the BCL-backed connectivity service shipped for consumers with no platform
/// implementation of their own (issue #47).
/// </summary>
/// <remarks>
/// The platform event cannot be raised from a test — <c>NetworkAvailabilityEventArgs</c>
/// has no accessible constructor — so these drive <c>PublishIfChanged</c>, the seam the
/// handler delegates to. Nothing here touches the real network: what is under test is the
/// publishing contract, not the BCL's reading of the interfaces.
/// </remarks>
public sealed class NetworkAvailabilityConnectivityServiceTests
{
    /// <summary>
    /// Puts the service in a known state before subscribing, so these tests do not depend
    /// on whether the machine running them happens to be online.
    /// </summary>
    private static NetworkAvailabilityConnectivityService Baseline(bool connected)
    {
        var service = new NetworkAvailabilityConnectivityService();
        service.PublishIfChanged(connected);
        return service;
    }

    [Fact]
    public void Subscribing_ReplaysNothing()
    {
        using var service = Baseline(connected: false);
        var observed = new List<bool>();

        using var subscription = service.ConnectivityChanged.Subscribe(new Recorder(observed));

        // A change stream, not a state view. Current state comes from IsConnected.
        Assert.Empty(observed);
    }

    [Fact]
    public void RepeatedSameValue_PublishesNothing()
    {
        using var service = Baseline(connected: true);
        var observed = new List<bool>();
        using var subscription = service.ConnectivityChanged.Subscribe(new Recorder(observed));

        service.PublishIfChanged(true);
        service.PublishIfChanged(true);

        // Republishing "connected" would trigger a flush with nothing new to send.
        Assert.Empty(observed);
    }

    [Fact]
    public void EachTransition_PublishesOnce()
    {
        using var service = Baseline(connected: true);
        var observed = new List<bool>();
        using var subscription = service.ConnectivityChanged.Subscribe(new Recorder(observed));

        service.PublishIfChanged(false);
        service.PublishIfChanged(false);
        service.PublishIfChanged(true);

        Assert.Equal([false, true], observed);
    }

    [Fact]
    public void EverySubscriber_SeesTheSameChange()
    {
        using var service = Baseline(connected: true);
        List<bool> first = [], second = [];
        using var a = service.ConnectivityChanged.Subscribe(new Recorder(first));
        using var b = service.ConnectivityChanged.Subscribe(new Recorder(second));

        service.PublishIfChanged(false);

        Assert.Equal([false], first);
        Assert.Equal([false], second);
    }

    [Fact]
    public void DisposedSubscription_StopsReceiving()
    {
        using var service = Baseline(connected: true);
        var observed = new List<bool>();
        var subscription = service.ConnectivityChanged.Subscribe(new Recorder(observed));

        subscription.Dispose();
        service.PublishIfChanged(false);

        Assert.Empty(observed);
    }

    [Fact]
    public void DisposingTheSubscriptionTwice_IsHarmless()
    {
        using var service = Baseline(connected: true);
        var subscription = service.ConnectivityChanged.Subscribe(new Recorder([]));

        subscription.Dispose();
        subscription.Dispose();
    }

    [Fact]
    public void Dispose_CompletesSubscribers_AndStopsPublishing()
    {
        var service = Baseline(connected: true);
        var observed = new List<bool>();
        var recorder = new Recorder(observed);
        using var subscription = service.ConnectivityChanged.Subscribe(recorder);

        service.Dispose();

        Assert.True(recorder.Completed);

        service.PublishIfChanged(false);
        Assert.Empty(observed);
    }

    [Fact]
    public void SubscribingAfterDispose_CompletesImmediately()
    {
        var service = Baseline(connected: true);
        service.Dispose();

        var recorder = new Recorder([]);
        using var subscription = service.ConnectivityChanged.Subscribe(recorder);

        Assert.True(recorder.Completed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var service = Baseline(connected: true);
        var recorder = new Recorder([]);
        using var subscription = service.ConnectivityChanged.Subscribe(recorder);

        service.Dispose();
        service.Dispose();

        // Completed once; a second Dispose must not re-notify a subscriber list it cleared.
        Assert.Equal(1, recorder.CompletedCount);
    }

    [Fact]
    public void Subscribe_NullObserver_Throws()
    {
        using var service = Baseline(connected: true);

        Assert.Throws<ArgumentNullException>(
            () => service.ConnectivityChanged.Subscribe(null!));
    }

    [Fact]
    public void IsConnected_AnswersWithoutThrowing()
    {
        using var service = Baseline(connected: true);

        // Whatever the answer on this machine, asking must not fail. The value itself is
        // the BCL's business, not ours.
        _ = service.IsConnected;
    }

    [Fact]
    public void SatisfiesTheRegistrationRequirement()
    {
        using var service = new NetworkAvailabilityConnectivityService();

        Assert.IsAssignableFrom<IConnectivityService>(service);
    }

    private sealed class Recorder(List<bool> observed) : IObserver<bool>
    {
        public int CompletedCount { get; private set; }
        public bool Completed => CompletedCount > 0;

        public void OnNext(bool value) => observed.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() => CompletedCount++;
    }
}
