using Hyperwyc.Sample.Maui.Services;

namespace Hyperwyc.Sample.Maui;

public partial class App : Application
{
    private readonly AuthenticationService _authService;

    public App(AuthenticationService authService)
    {
        _authService = authService;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(new AppShell(_authService));
}
