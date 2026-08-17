using System.Reactive.Subjects;
using Hyperwyc.Interfaces;

namespace Hyperwyc.Sample.Maui.Services;

public class MauiConnectivityService : IConnectivityService
{
    public bool IsConnected => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    private readonly BehaviorSubject<bool> _connectedSubject = new(false);

    public IObservable<bool> ConnectivityChanged => _connectedSubject;

    public MauiConnectivityService()
    {
        Connectivity.Current.ConnectivityChanged += (sender, args) =>
        {
            var connected = args.NetworkAccess == NetworkAccess.Internet;
            _connectedSubject.OnNext(connected);
        };
    }
}
