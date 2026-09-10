using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hyperwyc.Interfaces;
using Hyperwyc.Models;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class DiagnosticsViewModel(IHyperwycDiagnostics diagnostics) : ObservableObject
{
    public ObservableCollection<PendingItem> Outbox { get; set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [RelayCommand]
    public async Task GetPendingItems()
    {
        IsLoading = true;

        var items = await diagnostics.GetPendingOutboxAsync();

        Outbox.Clear();
        foreach (var item in items)
        {
            Outbox.Add(item);
        }

        IsLoading = false;
    }
}
