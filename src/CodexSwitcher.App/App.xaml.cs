using CodexSwitcher.App.Composition;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Usage.Services;
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
            var tracer = new ShutdownTracer();
            tracer.Start();

            try
            {
                // Phase 1: Request stopping and cancel background operations
                tracer.MeasurePhase("CancelBackgroundOperations", () =>
                {
                    try
                    {
                        var appLifetime = _host.Services.GetService<IAppLifetime>();
                        appLifetime?.StopApplication();
                    }
                    catch { }

                    try
                    {
                        var usagePolling = _host.Services.GetService<UsagePollingCoordinator>();
                        usagePolling?.Stop();
                    }
                    catch { }
                });

                // Phase 2: Terminate owned child processes promptly in parallel
                await tracer.MeasurePhaseAsync("TerminateOwnedProcesses", async () =>
                {
                    var processRegistry = _host.Services.GetService<ISwitchboardCodexProcessRegistry>();
                    if (processRegistry is not null)
                    {
                        try
                        {
                            using var termCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                            await processRegistry.TerminateAllOwnedProcessesAsync(TimeSpan.FromMilliseconds(300), termCts.Token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }).ConfigureAwait(false);

                // Phase 3: Stop Host with bounded timeout
                await tracer.MeasurePhaseAsync("StopHost", async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                    await _host.StopAsync(cts.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort graceful host shutdown
            }
            finally
            {
                tracer.MeasurePhase("DisposeHost", () =>
                {
                    _host.Dispose();
                    _host = null;
                });

                System.Diagnostics.Debug.WriteLine(tracer.FormatSummary());
            }
        }
    }
}
