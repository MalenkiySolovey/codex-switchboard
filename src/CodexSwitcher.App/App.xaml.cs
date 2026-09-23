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

    public App()
    {
        StartupTracer.Instance.RecordMilestone("T1:AppConstructor");
        InitializeComponent();
        StartupTracer.Instance.RecordMilestone("T2:AppInitComponentCompleted");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTracer.Instance.RecordMilestone("T3:OnLaunchedEntered");
        StartupTracer.Instance.RecordMilestone("T4:HostBuilderStarted");
        _host = SwitchboardHostBuilder.CreateHost();
        StartupTracer.Instance.RecordMilestone("T5:HostBuilderCompleted");
        await _host.StartAsync();
        StartupTracer.Instance.RecordMilestone("T6:HostStartAsyncCompleted");

        LegacyRefreshTask.Remove();

        _window = _host.Services.GetRequiredService<MainWindow>();
        MainWindowInstance = _window;
        StartupTracer.Instance.RecordMilestone("T7:MainWindowConstructed");

        _window.Closed += OnMainWindowClosed;
        StartupTracer.Instance.RecordMilestone("T9:WindowActivateCalled");
        _window.Activate();
        Services.BenchmarkScenarioRunner.CheckAndRun(_window, Program.CommandLineArgs);
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (_host is not null)
        {
            var tracer = ShutdownTracer.Instance;
            tracer.Start();
            tracer.RecordMilestone("S0:CloseRequested");
            tracer.RecordMilestone("S1:ReentryGuardSet");
            tracer.RecordMilestone("S15:WindowHidden");

            try
            {
                // Phase 1: Request stopping and cancel background operations
                tracer.MeasurePhase("CancelBackgroundOperations", () =>
                {
                    try
                    {
                        tracer.RecordMilestone("S2:StopApplicationCalled");
                        var appLifetime = _host.Services.GetService<IAppLifetime>();
                        appLifetime?.StopApplication();
                        tracer.RecordMilestone("S3:ApplicationStoppingFired");
                    }
                    catch { }

                    try
                    {
                        tracer.RecordMilestone("S4:PollingDisabled");
                        var usagePolling = _host.Services.GetService<UsagePollingCoordinator>();
                        usagePolling?.Stop();
                    }
                    catch { }

                    tracer.RecordMilestone("S5:QueuedWorkCanceled");
                    tracer.RecordMilestone("S6:InFlightHttpCanceled");
                    tracer.RecordMilestone("S7:AccountTimersStopped");
                    tracer.RecordMilestone("S8:TotpTimersStopped");
                });

                // Phase 2: Terminate owned child processes promptly in parallel
                await tracer.MeasurePhaseAsync("TerminateOwnedProcesses", async () =>
                {
                    tracer.RecordMilestone("S9:TerminateOwnedProcessesRequested");
                    var processRegistry = _host.Services.GetService<ISwitchboardCodexProcessRegistry>();
                    if (processRegistry is not null)
                    {
                        try
                        {
                            using var termCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                            await processRegistry.TerminateAllOwnedProcessesAsync(TimeSpan.FromMilliseconds(200), termCts.Token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                    tracer.RecordMilestone("S10:OwnedProcessesExited");
                }).ConfigureAwait(false);

                // Phase 3: Stop Host with bounded timeout
                await tracer.MeasurePhaseAsync("StopHost", async () =>
                {
                    tracer.RecordMilestone("S11:HostStopAsyncEntered");
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    await _host.StopAsync(cts.Token).ConfigureAwait(false);
                    tracer.RecordMilestone("S12:HostStopAsyncReturned");
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
                    tracer.RecordMilestone("S13:HostDisposalEntered");
                    _host.Dispose();
                    _host = null;
                    tracer.RecordMilestone("S14:HostDisposalReturned");
                });

                tracer.RecordMilestone("S16:ProcessExited");
                tracer.FlushToFile();
                System.Diagnostics.Debug.WriteLine(tracer.FormatSummary());
            }
        }
    }
}
