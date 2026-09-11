using CodexSwitcher.App.Composition;
using CodexSwitcher.Infra.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

namespace CodexSwitcher.App;

/// <summary>
/// Application entry point and Generic Host lifetime owner.
/// Coordinates host startup and bounded graceful shutdown upon main window closure.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _window;

    public static MainWindow? MainWindowInstance { get; private set; }

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _host = SwitchboardHostBuilder.CreateHost();
        await _host.StartAsync();

        LegacyRefreshTask.Remove();

        _window = _host.Services.GetRequiredService<MainWindow>();
        MainWindowInstance = _window;

        _window.Closed += OnMainWindowClosed;
        _window.Activate();
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (_host is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _host.StopAsync(cts.Token);
            }
            catch
            {
                // Best-effort graceful host shutdown
            }
            finally
            {
                _host.Dispose();
                _host = null;
            }
        }
    }
}
