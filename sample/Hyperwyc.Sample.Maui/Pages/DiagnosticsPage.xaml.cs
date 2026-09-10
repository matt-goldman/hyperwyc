using Hyperwyc.Sample.Maui.ViewModels;

namespace Hyperwyc.Sample.Maui.Pages;

public partial class DiagnosticsPage : ContentPage
{
    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

