using Hyperwyc.Sample.Maui.ViewModels;

namespace Hyperwyc.Sample.Maui.Pages;

public partial class NewSalePage : ContentPage
{
    private readonly NewSaleViewModel _viewModel;

    public NewSalePage(NewSaleViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
    }

    private void InputView_OnTextChanged(object? sender, TextChangedEventArgs e) => _viewModel.ValidateSale();
}

