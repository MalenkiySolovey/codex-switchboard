using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace CodexSwitcher.App;

/// <summary>
/// Application entry point providing single-instance registration and activation redirection.
/// </summary>
public static class Program
{
    public const string SingleInstanceKey = "CodexSwitchboard.SingleInstance";

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static void Main(string[] args)
    {
        if (args != null && args.Length > 0 && Array.Exists(args, a => a.Equals("--diagnose-windows-auth", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(-1);
            Console.WriteLine(Services.WindowsPasswordVerificationService.RunSanitizedDiagnosticProbe());
            return;
        }

        ComWrappersSupport.InitializeComWrappers();

        var isRedirect = DecideRedirection();
        if (isRedirect)
        {
            Environment.Exit(0);
            return;
        }

        Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool DecideRedirection()
    {
        var currentInstance = AppInstance.GetCurrent();
        var args = currentInstance.GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

        if (!keyInstance.IsCurrent)
        {
            // Execute on thread pool (MTA) with timeout to prevent STA apartment COM deadlocks
            try
            {
                Task.Run(async () =>
                {
                    await keyInstance.RedirectActivationToAsync(args);
                }).Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Best-effort redirection; ensure process exits without blocking
            }
            return true;
        }

        keyInstance.Activated += OnRedirectedActivated;
        return false;
    }

    private static void OnRedirectedActivated(object? sender, AppActivationArguments args)
    {
        if (App.MainWindowInstance is { } window)
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                window.BringToFront();
            });
        }
    }
}
