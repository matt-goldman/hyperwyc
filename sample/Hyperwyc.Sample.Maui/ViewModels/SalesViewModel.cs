using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Hyperwyc.Sample.Maui.Services;
using Shared;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class SalesViewModel(SalesApiClient client) : ObservableObject
{
    public ObservableCollection<Sale> Sales { get; set; } = [];

    public async Task LoadApiSales()
    {
        var apiSales = await client.GetSalesAsync();

        Sales = new ObservableCollection<Sale>(apiSales);
    }
}
