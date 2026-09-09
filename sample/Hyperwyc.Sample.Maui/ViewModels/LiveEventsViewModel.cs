using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Mvvm.ComponentModel;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class LiveEventsViewModel : ObservableObject, IDisposable, IObserver<HyperwycEvent>
{
    private readonly IDisposable _eventSubscription;

    public ObservableCollection<EventViewModel> LiveEvents { get; set; } = [];

    public LiveEventsViewModel(IHyperwyc hyperwyc)
    {
        _eventSubscription = hyperwyc.Events.Subscribe(this);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _eventSubscription.Dispose();
    }

    public void OnCompleted() => LiveEvents.Clear();

    public async void OnError(Exception error)
    {
        var text = $"Error sending request: {error.Message}";

        var toast = Toast.Make(text);

        await toast.Show(CancellationToken.None);
    }

    public void OnNext(HyperwycEvent value)
    {
        LiveEvents.Add(new EventViewModel(value));

        //if (value.Outcome?.StatusCode is null or (>= 200 and < 300)) return;

        var message = $"A {value.Method} call to {value.Url} has failed: {value.Outcome?.Kind}";
        var toast = Toast.Make(message);
        toast.Show();
    }
}


public class EventViewModel
{
    public string Url { get; }

    public bool IsSuccess { get; }

    public Color TitleColor { get; }

    public string TimeSent { get; }

    public string EventType { get; }

    public string Method { get; }

    public string Title { get; }


    public EventViewModel(HyperwycEvent syncEvent)
    {
        Url = syncEvent.Url;

        // The status code, not the event: Hyperwyc reports that the server answered and takes no
        // view on what it said, so deciding whether that is good news is the app's job. See
        // ADR 0010.
        IsSuccess = syncEvent.Outcome?.StatusCode is >= 200 and < 300;

        TitleColor = IsSuccess ? Colors.Green : Colors.Red;

        TimeSent = syncEvent.Timestamp.ToString("HH:mm");
        EventType = syncEvent.Type.ToString();
        Method = syncEvent.Method;

        Title = IsSuccess
            ? "Succeeded"
            : syncEvent.Outcome?.ReasonPhrase ?? syncEvent.Outcome?.Kind.ToString() ?? "Unknown";
    }
}
