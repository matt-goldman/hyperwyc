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
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            await Application.Current!.Windows[0].Page!.DisplayAlertAsync("Error", e.Message, "OK");
        }
    }
}
