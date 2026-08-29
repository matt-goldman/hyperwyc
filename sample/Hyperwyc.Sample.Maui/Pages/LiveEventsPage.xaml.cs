using Hyperwyc.Sample.Maui.ViewModels;

namespace Hyperwyc.Sample.Maui.Pages;

public partial class LiveEventsPage : ContentPage
{
    public LiveEventsPage(LiveEventsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

