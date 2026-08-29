using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Mvvm.ComponentModel;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class LiveEventsViewModel : ObservableObject, IDisposable, IObserver<SyncEvent>
{
    private readonly IDisposable _eventSubscription;

    public ObservableCollection<EventViewModel> LiveEvents { get; set; } = [];

    public LiveEventsViewModel(IHyperwyc hyperwyc)
    {
        _eventSubscription = hyperwyc.SyncEvents.Subscribe(this);
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

    public void OnNext(SyncEvent value) => LiveEvents.Add(new EventViewModel(value));
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


    public EventViewModel(SyncEvent syncEvent)
    {
        Url = syncEvent.Url;

        IsSuccess = syncEvent.Outcome?.Kind == SyncOutcomeKind.Succeeded;

        TitleColor = IsSuccess ? Colors.Green : Colors.Red;

        TimeSent = syncEvent.Timestamp.ToString("HH:mm");
        EventType = syncEvent.Type.ToString();
        Method = syncEvent.Method;

        Title = IsSuccess ? "Succeeded" : syncEvent.Outcome?.ReasonPhrase ?? "Unknown";
    }
}
