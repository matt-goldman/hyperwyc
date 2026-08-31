using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hyperwyc.Interfaces;
using Hyperwyc.Sample.Maui.Services;
using Shared;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class NewSaleViewModel(SalesApiClient client, IConnectivityService connectivity) : ObservableObject, IQueryAttributable
{
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool ProductMissing { get; set; }

    [ObservableProperty]
    public partial bool SaleEnabled { get; set; }

    [ObservableProperty]
    public partial bool HasProduct { get; set; }

    [ObservableProperty]
    public partial int QuantityToSell { get; set; }

    [ObservableProperty]
    public partial Product? Product { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        Product = query["product"] as Product;
        ProductMissing = Product == null;
        HasProduct = !ProductMissing;
    }

    public void ValidateSale() => SaleEnabled = QuantityToSell > 0 && QuantityToSell <= Product?.StockLevel;

    [RelayCommand]
    private async Task RecordSale()
    {
        IsLoading = true;

        try
        {
            var sale = new Sale() { SoldAt = DateTime.UtcNow, Quantity = QuantityToSell, ProductId = Product!.Id, };

            var result = await client.RecordSaleAsync(sale);

            // TODO: this is wrong. Evaluate against created (api) vs accepted (hyperwyc).
            //       The API already returns the proper form as well as the object so we
            //       could keep this validation for created only. TBD.
            //       For now branch on connectivity status.
            //       Need to update sync event response to alert when a sale failed after
            //       connectivity was restored.

            var isValid = !connectivity.IsConnected || ValidateSale(sale, result);

            var title = isValid ? "Sale Recorded" : "Sale Failed";

            var message = isValid
                ? $"Sale of {sale.Quantity} x {Product.Name} successfully recorded"
                : "Something went wrong and the sale could not be recorded";

            await Application.Current!.Windows[0].Page!.DisplayAlertAsync(title, message, "Ok");

            if (isValid)
            {
                QuantityToSell = 0;
                await Shell.Current.GoToAsync("..");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            await Application.Current!.Windows[0].Page!.DisplayAlertAsync("Error", e.Message, "Ok");
        }
        finally
        {
            IsLoading = false;
        }
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
