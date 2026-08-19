using Hyperwyc.Sample.Maui.ViewModels;

namespace Hyperwyc.Sample.Maui.Pages;

public partial class CataloguePage : ContentPage
{
    private readonly CatalogueViewModel _viewModel;

    public CataloguePage(CatalogueViewModel catalogueViewModel)
    {
        InitializeComponent();
        _viewModel = catalogueViewModel;
        BindingContext = _viewModel;
    }

    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        await _viewModel.LoadProducts();
        base.OnNavigatedTo(args);
    }
}

