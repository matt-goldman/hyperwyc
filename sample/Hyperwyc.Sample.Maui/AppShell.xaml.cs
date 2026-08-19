using Hyperwyc.Sample.Maui.Pages;
using Plugin.Maui.SmartNavigation.Extensions;

namespace Hyperwyc.Sample.Maui;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("Sales/new", typeof(NewSalePage));
    }

    private async void LoginMenuItem_OnClicked(object? sender, EventArgs e) => await Navigation.PushModalAsync<LoginPage>();
}
