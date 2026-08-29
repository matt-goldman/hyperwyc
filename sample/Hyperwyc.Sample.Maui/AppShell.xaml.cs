using Hyperwyc.Sample.Maui.Pages;
using Hyperwyc.Sample.Maui.Services;
using Plugin.Maui.SmartNavigation.Extensions;

namespace Hyperwyc.Sample.Maui;

public partial class AppShell : Shell
{
    private readonly AuthenticationService _authService;
    private bool _isLoggedIn = false;

    public AppShell(AuthenticationService authService)
    {
        _authService = authService;
        InitializeComponent();
        Routing.RegisterRoute("Sales/new", typeof(NewSalePage));
        authService.LoginStateChanged += UpdateLoginState;
    }

    private async void LoginMenuItem_OnClicked(object? sender, EventArgs e)
    {
        if (_isLoggedIn)
        {
            await _authService.Logout();
        }
        else
        {
            await Navigation.PushModalAsync<LoginPage>();
        }
    }


    private void UpdateLoginState(object? sender, AuthenticationService.LoginStateEventArgs e)
    {
        _isLoggedIn = e.IsLoggedIn;

        LoginMenuItem.Text = e.IsLoggedIn ? "Logout" : "Login";
    }
}
