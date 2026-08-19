using Hyperwyc.Sample.Maui.ViewModels;

namespace Hyperwyc.Sample.Maui.Pages;

public partial class LoginPage : ContentPage
{
    public LoginPage(LoginViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}

