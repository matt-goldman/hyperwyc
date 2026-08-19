using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hyperwyc.Sample.Maui.Services;
using Shared;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class NewSaleViewModel(SalesApiClient client) : ObservableObject, IQueryAttributable
{
    [ObservableProperty]
    public partial bool ProductMissing { get; set; }

    [ObservableProperty]
    public partial int QuantityToSell { get; set; }

    [ObservableProperty]
    public partial Product? Product { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        Product = query["product"] as Product;
        ProductMissing = Product == null;
    }

    [RelayCommand]
    private async Task RecordSale()
    {
        var sale = new Sale()
        {
            SoldAt      =  DateTime.UtcNow,
            Quantity    =  QuantityToSell,
            ProductId   =  Product!.Id,
        };

        var result = await client.RecordSaleAsync(sale);

        var isValid = ValidateSale(sale, result);

        var title = isValid ? "Sale Recorded" : "Sale Failed";

        var message = isValid ? $"Sale of {sale.Quantity} x {Product.Name} successfully recorded" : "Something went wrong and the sale could not be recorded";

        await Application.Current!.Windows[0].Page!.DisplayAlertAsync(title, message, "Ok");
    }

    private static bool ValidateSale(Sale soldItem, Sale? returnedItem)
    {
        if (returnedItem is null) return false;

        return soldItem.Id == returnedItem.Id
            && soldItem.SoldAt == returnedItem.SoldAt
            && soldItem.Quantity == returnedItem.Quantity
            && soldItem.ProductId == returnedItem.ProductId;

    }
}
