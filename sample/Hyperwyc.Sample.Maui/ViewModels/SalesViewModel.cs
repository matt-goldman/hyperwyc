using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Hyperwyc.Sample.Maui.Services;
using Shared;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class SalesViewModel(
    SalesApiClient client,
    AuthenticationService authService) : ObservableObject
{
    public ObservableCollection<Sale> Sales { get; set; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public async Task LoadApiSales()
    {
        var isLoggedIn = await authService.GetIsLoggedInAsync();

        if (isLoggedIn)
        {
            IsLoading = true;

            var apiSales = await client.GetSalesAsync();

            Sales.Clear();
            foreach (var sale in apiSales)
            {
                Sales.Add(sale);
            }

            IsLoading = false;
        }
        else
        {
            await Application.Current!.Windows[0].Page!.DisplayAlertAsync("Not logged in", "You must be logged in to view sales. Please log in via the menu.", "OK");
            await Shell.Current.GoToAsync("..");
        }
    }
}
