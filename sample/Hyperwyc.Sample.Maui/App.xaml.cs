using Microsoft.Extensions.DependencyInjection;

namespace Hyperwyc.Sample.Maui;

public partial class App : Application
{
    private readonly AppShell _shell;

    public App(AppShell shell)
    {
        _shell = shell;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) => new(_shell);
}
