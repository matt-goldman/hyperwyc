using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hyperwyc.Sample.Maui.Services;
using Shared;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class CatalogueViewModel(ProductsApiClient productsClient) : ObservableObject
{
    public ObservableCollection<Product> Products { get; set; } = [];

    public async Task LoadProducts()
    {
        var apiProducts = await productsClient.GetProductsAsync();
        Products = new ObservableCollection<Product>(apiProducts);
    }

    [RelayCommand]
    private static Task SellProduct(Product product) => Shell.Current.GoToAsync("//sales/new", new ShellNavigationQueryParameters { { "product", product } });
}
