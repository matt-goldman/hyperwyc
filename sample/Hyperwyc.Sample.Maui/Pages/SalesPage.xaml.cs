using Hyperwyc.Sample.Maui.ViewModels;

namespace Hyperwyc.Sample.Maui.Pages;

public partial class SalesPage : ContentPage
{
    private readonly SalesViewModel _viewModel;

    public SalesPage(SalesViewModel salesViewModel)
    {
        InitializeComponent();
        _viewModel = salesViewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        await _viewModel.LoadApiSales();
        base.OnNavigatedTo(args);
    }
}

