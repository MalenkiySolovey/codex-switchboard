using CodexSwitcher.App.Composition;
using CodexSwitcher.Infra.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

using CodexSwitcher.Core.Routing.Contracts;

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
                var processRegistry = _host.Services.GetService<ISwitchboardCodexProcessRegistry>();
                if (processRegistry is not null)
                {
                    try
                    {
                        using var termCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
                        await processRegistry.TerminateAllOwnedProcessesAsync(TimeSpan.FromMilliseconds(1000), termCts.Token);
                    }
                    catch
                    {
                        // Best-effort owned process cleanup
                    }
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
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
