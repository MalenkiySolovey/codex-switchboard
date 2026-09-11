using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.App.Services;
using Microsoft.UI.Xaml;

namespace CodexSwitcher.App.Shell.Windowing;

/// <summary>
/// Coordinates window platform events with application and feature lifecycles.
/// Guarantees:
/// 1. Immediate hiding of revealed TOTP codes on window deactivation / minimize.
/// 2. Fast non-blocking shutdown on Window.Closed without waiting on network/HTTP polling.
/// 3. Invalidation of temporary Windows Hello / auth sessions on app termination.
/// </summary>
public sealed class WindowLifecycleCoordinator
{
    private readonly ITotpRevealAuthorizationService? _authService;
    private int _isClosed;

    public WindowLifecycleCoordinator(ITotpRevealAuthorizationService? authService = null)
    {
        _authService = authService;
    }

    public void HandleActivation(WindowActivationState state, Action hideTotp)
    {
        if (state == WindowActivationState.Deactivated)
        {
            hideTotp();
        }
    }

    public void HandleForegroundChange(
        bool isVisible,
        bool isMinimized,
        Action<bool> setForegroundActive,
        Action hideTotp)
    {
        bool active = isVisible && !isMinimized;
        if (!active)
        {
            hideTotp();
        }
        setForegroundActive(active);
    }

    public void HandleWindowClosed(Action hideTotp, Action cleanup)
    {
        if (Interlocked.Exchange(ref _isClosed, 1) != 0)
            return; // Idempotent close

        try
        {
            hideTotp();
            _authService?.Invalidate();
            cleanup();
        }
        catch
        {
            // Best effort cleanup during process exit
        }

        AppHost.Shutdown();
        Application.Current?.Exit();
        Environment.Exit(0);
    }
}
