using CodexSwitcher.App.Services;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Core.Services;
using Microsoft.UI.Xaml;

namespace CodexSwitcher.App;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppHost.Build();
        LegacyRefreshTask.Remove();

        _window = new MainWindow();
        _window.Activate();
    }
}
