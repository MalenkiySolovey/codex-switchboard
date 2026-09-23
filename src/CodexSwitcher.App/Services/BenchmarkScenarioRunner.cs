using System.Diagnostics;
using CodexSwitcher.App.Views;
using CodexSwitcher.Core.Common.Lifecycle;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Development/benchmark scenario runner for executing automated startup and shutdown perf measurements (A-G).
/// Strictly excludes credentials, emails, and sensitive arguments.
/// </summary>
public static class BenchmarkScenarioRunner
{
    public static void CheckAndRun(MainWindow window, string[] args)
    {
        if (args == null || args.Length == 0) return;

        var scenarioArg = args.FirstOrDefault(a => a.StartsWith("--perf-scenario=", StringComparison.OrdinalIgnoreCase));
        var delayArg = args.FirstOrDefault(a => a.StartsWith("--perf-auto-close-delay-ms=", StringComparison.OrdinalIgnoreCase));

        if (scenarioArg == null && delayArg == null) return;

        var scenario = scenarioArg != null ? scenarioArg.AsSpan("--perf-scenario=".Length).Trim().ToString().ToUpperInvariant() : "IDLE";
        var delayMs = 0;
        if (delayArg != null && int.TryParse(delayArg.AsSpan("--perf-auto-close-delay-ms=".Length), out var parsed))
        {
            delayMs = parsed;
        }

        Task.Run(async () =>
        {
            try
            {
                await RunScenarioInternalAsync(window, scenario, delayMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BenchmarkScenarioRunner] Error: {ex.Message}");
            }
        });
    }

    private static async Task RunScenarioInternalAsync(MainWindow window, string scenario, int delayMs)
    {
        if (scenario == "G") // Immediately after startup
        {
            await Task.Delay(Math.Max(10, delayMs)).ConfigureAwait(false);
            window.DispatcherQueue.TryEnqueue(() => window.Close());
            return;
        }

        // Wait for T12: UI interactive milestone
        var maxWait = Stopwatch.StartNew();
        while (StartupTracer.Instance.GetMilestoneElapsed("T12:UIInteractive") == null && maxWait.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (scenario == "A" || scenario == "IDLE") // Idle
        {
            await Task.Delay(Math.Max(500, delayMs)).ConfigureAwait(false);
            window.DispatcherQueue.TryEnqueue(() => window.Close());
            return;
        }

        var shellVm = window.ShellRoot?.ViewModel;
        if (shellVm == null)
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            window.DispatcherQueue.TryEnqueue(() => window.Close());
            return;
        }

        if (scenario == "B") // Refresh All
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                shellVm.Accounts.RefreshAllCommand.Execute(null);
            });
            await Task.Delay(100).ConfigureAwait(false);
            window.DispatcherQueue.TryEnqueue(() => window.Close());
            return;
        }

        if (scenario == "C") // Two usage workers active
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                shellVm.Accounts.RefreshAllCommand.Execute(null);
            });
            await Task.Delay(150).ConfigureAwait(false);
            window.DispatcherQueue.TryEnqueue(() => window.Close());
            return;
        }

        if (scenario == "D") // Queued 10+ account refreshes
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                shellVm.Accounts.RefreshAllCommand.Execute(null);
            });
            await Task.Delay(50).ConfigureAwait(false);
            window.DispatcherQueue.TryEnqueue(() => window.Close());
            return;
        }

        if (scenario == "E") // Provider HTTP request active
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                var firstProvider = shellVm.ApiProviders.Items.FirstOrDefault();
                if (firstProvider != null)
                {
                    shellVm.ApiProviders.DiscoverModelsCommand.Execute(firstProvider);
                }
            });
            await Task.Delay(100).ConfigureAwait(false);
            window.DispatcherQueue.TryEnqueue(() => window.Close());
            return;
        }

        if (scenario == "F") // Immediately after normal switch completes
        {
            var firstItem = shellVm.Accounts.Items.FirstOrDefault(a => a.CanSwitch);
            if (firstItem != null)
            {
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    shellVm.Accounts.SwitchCommand.Execute(firstItem);
                });
                await Task.Delay(300).ConfigureAwait(false);
            }
            window.DispatcherQueue.TryEnqueue(() => window.Close());
            return;
        }

        // Default: wait specified delay and close
        await Task.Delay(Math.Max(500, delayMs)).ConfigureAwait(false);
        window.DispatcherQueue.TryEnqueue(() => window.Close());
    }
}
