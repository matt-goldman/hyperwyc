using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hyperwyc.Sample.Maui.Services;
using Shared;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class CatalogueViewModel(
    ProductsApiClient productsClient,
    AuthenticationService authService) : ObservableObject
{
    public ObservableCollection<Product> Products { get; set; } = [];

    [ObservableProperty]
    public partial Product? SelectedProduct { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public async Task LoadProducts()
    {
        IsLoading = true;
        var apiProducts = await productsClient.GetProductsAsync();
        Products.Clear();
        foreach (var apiProduct in apiProducts)
        {
            Products.Add(apiProduct);
        }
        IsLoading = false;
    }

    [RelayCommand]
    private async Task SellProduct()
    {
        if (SelectedProduct == null) return;

        var isLoggedIn = await authService.GetIsLoggedInAsync();

        if (isLoggedIn)
        {
            await Shell.Current.GoToAsync("sales/new", new ShellNavigationQueryParameters { { "product", SelectedProduct } });
        }
        else
        {
            await Application.Current!.Windows[0].Page!.DisplayAlertAsync("Not logged in",
                "You must be logged in to sell a product, please log in via the menu.", "Ok");
        }
    }
}
