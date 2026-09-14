using Hyperwyc.Models;
using Xunit;

namespace Hyperwyc.Tests;

public class HyperwycEventStreamTests
{
    private static HyperwycEvent MakeEvent(HyperwycEventType type = HyperwycEventType.OnQueued) =>
        new()
        {
            Type      = type,
            Url       = "https://example.com/api/orders",
            Method    = "POST",
            Timestamp = DateTimeOffset.UtcNow,
        };

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Simple observer that records received events and terminal signals.</summary>
    private sealed class RecordingObserver : IObserver<HyperwycEvent>
    {
        private readonly List<HyperwycEvent> _events = [];
        public IReadOnlyList<HyperwycEvent> Events => _events;
        public bool Completed { get; private set; }
        public Exception? Error { get; private set; }

        public void OnNext(HyperwycEvent value) => _events.Add(value);
        public void OnCompleted() => Completed = true;
        public void OnError(Exception error) => Error = error;
    }

    // -------------------------------------------------------------------------
    // Subscribe / receive
    // -------------------------------------------------------------------------

    [Fact]
    public void Publish_SingleSubscriber_ReceivesEvent()
    {
        using var stream = new HyperwycEventStream();
        var observer = new RecordingObserver();
        stream.Subscribe(observer);

        var evt = MakeEvent();
        stream.Publish(evt);

        Assert.Single(observer.Events);
        Assert.Same(evt, observer.Events[0]);
    }

    [Fact]
    public void Publish_MultipleEvents_ReceivedInOrder()
    {
        using var stream = new HyperwycEventStream();
        var observer = new RecordingObserver();
        stream.Subscribe(observer);

        var e1 = MakeEvent(HyperwycEventType.OnQueued);
        var e2 = MakeEvent(HyperwycEventType.OnDelivered);
        stream.Publish(e1);
        stream.Publish(e2);

        Assert.Equal(2, observer.Events.Count);
        Assert.Same(e1, observer.Events[0]);
        Assert.Same(e2, observer.Events[1]);
    }

    // -------------------------------------------------------------------------
    // Unsubscribe
    // -------------------------------------------------------------------------

    [Fact]
    public void Dispose_Subscription_StopsReceivingEvents()
    {
        using var stream = new HyperwycEventStream();
        var observer = new RecordingObserver();
        var sub = stream.Subscribe(observer);

        sub.Dispose();
        stream.Publish(MakeEvent());

        Assert.Empty(observer.Events);
    }

    [Fact]
    public void Dispose_Subscription_Twice_DoesNotThrow()
    {
        using var stream = new HyperwycEventStream();
        var observer = new RecordingObserver();
        var sub = stream.Subscribe(observer);

        sub.Dispose();
        sub.Dispose(); // second dispose should be a no-op
    }

    // -------------------------------------------------------------------------
    // Multi-subscriber fan-out
    // -------------------------------------------------------------------------

    [Fact]
    public void Publish_MultipleSubscribers_AllReceiveEvent()
    {
        using var stream = new HyperwycEventStream();
        var o1 = new RecordingObserver();
        var o2 = new RecordingObserver();
        var o3 = new RecordingObserver();
        stream.Subscribe(o1);
        stream.Subscribe(o2);
        stream.Subscribe(o3);

        stream.Publish(MakeEvent());

        Assert.Single(o1.Events);
        Assert.Single(o2.Events);
        Assert.Single(o3.Events);
    }

    [Fact]
    public void Publish_AfterOneUnsubscribes_OnlyRemainingSubscribersReceive()
    {
        using var stream = new HyperwycEventStream();
        var o1 = new RecordingObserver();
        var o2 = new RecordingObserver();
        stream.Subscribe(o1);
        var sub2 = stream.Subscribe(o2);

        sub2.Dispose();
        stream.Publish(MakeEvent());

        Assert.Single(o1.Events);
        Assert.Empty(o2.Events);
    }

    // -------------------------------------------------------------------------
    // Subscriber OnNext exception isolation
    // -------------------------------------------------------------------------

    [Fact]
    public void Publish_SubscriberThrows_OtherSubscribersStillReceive()
    {
        using var stream = new HyperwycEventStream();

        var faultyObserver = new FaultyObserver();
        var goodObserver = new RecordingObserver();
        stream.Subscribe(faultyObserver);
        stream.Subscribe(goodObserver);

        stream.Publish(MakeEvent()); // should not propagate the exception

        Assert.Single(goodObserver.Events);
    }

    private sealed class FaultyObserver : IObserver<HyperwycEvent>
    {
        public void OnNext(HyperwycEvent value) => throw new InvalidOperationException("boom");
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    // -------------------------------------------------------------------------
    // Dispose (stream)
    // -------------------------------------------------------------------------

    [Fact]
    public void Dispose_Stream_CallsOnCompletedOnAllSubscribers()
    {
        var stream = new HyperwycEventStream();
        var o1 = new RecordingObserver();
        var o2 = new RecordingObserver();
        stream.Subscribe(o1);
        stream.Subscribe(o2);

        stream.Dispose();

        Assert.True(o1.Completed);
        Assert.True(o2.Completed);
    }

    [Fact]
    public void Dispose_Stream_PublishIsNoOp()
    {
        var stream = new HyperwycEventStream();
        var observer = new RecordingObserver();
        stream.Subscribe(observer);
        stream.Dispose();

        stream.Publish(MakeEvent()); // should not throw or deliver

        Assert.Empty(observer.Events);
    }

    [Fact]
    public void Dispose_Stream_Twice_DoesNotThrow()
    {
        var stream = new HyperwycEventStream();
        stream.Dispose();
        stream.Dispose(); // should be idempotent
    }

    [Fact]
    public void Subscribe_AfterDispose_ImmediatelyCallsOnCompleted()
    {
        var stream = new HyperwycEventStream();
        stream.Dispose();

        var observer = new RecordingObserver();
        stream.Subscribe(observer);

        Assert.True(observer.Completed);
    }

    // -------------------------------------------------------------------------
    // PublishError
    // -------------------------------------------------------------------------

    [Fact]
    public void PublishError_CallsOnErrorOnAllSubscribers()
    {
        using var stream = new HyperwycEventStream();
        var o1 = new RecordingObserver();
        var o2 = new RecordingObserver();
        stream.Subscribe(o1);
        stream.Subscribe(o2);

        var ex = new Exception("stream error");
        stream.PublishError(ex);

        Assert.Same(ex, o1.Error);
        Assert.Same(ex, o2.Error);
    }

    [Fact]
    public void PublishError_ClearsSubscribers_SubsequentPublishDeliveredToNone()
    {
        using var stream = new HyperwycEventStream();
        var observer = new RecordingObserver();
        stream.Subscribe(observer);

        stream.PublishError(new Exception("boom"));
        stream.Publish(MakeEvent());

        Assert.Empty(observer.Events);
    }

    // -------------------------------------------------------------------------
    // IObservable<T> contract via Subscribe(Action<T>) extension
    // -------------------------------------------------------------------------

    [Fact]
    public void IsAssignableAsIObservable()
    {
        var stream = new HyperwycEventStream();
        IObservable<HyperwycEvent> observable = stream;
        Assert.NotNull(observable);
        stream.Dispose();
    }
}
