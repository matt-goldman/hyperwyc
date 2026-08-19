using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hyperwyc.Sample.Maui.Services;

namespace Hyperwyc.Sample.Maui.ViewModels;

public partial class LoginViewModel(AuthenticationService authService) : ObservableObject
{
    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [RelayCommand]
    public async Task Login()
    {
        try
        {
            await authService.LoginAsync(Email, Password);
            await Application.Current!.Windows[0].Page!.DisplayAlertAsync("Success", "You are now logged in.", "OK");
            await Application.Current!.Windows[0].Page!.Navigation.PopModalAsync();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            await Application.Current!.Windows[0].Page!.DisplayAlertAsync("Error", e.Message, "OK");
        }
    }

    [RelayCommand]
    public async Task Register()
    {
        try
        {
            await authService.RegisterAsync(Email, Password);
            await Application.Current!.Windows[0].Page!.DisplayAlertAsync("Success", "You are now registered, please login", "OK");
        }
        catch (Exception)
        {
            await Application.Current!.Windows[0].Page!.DisplayAlertAsync("Error", "Couldn't register you, please make sure you are online and not using an existing account.", "OK");
        }
    }
}
