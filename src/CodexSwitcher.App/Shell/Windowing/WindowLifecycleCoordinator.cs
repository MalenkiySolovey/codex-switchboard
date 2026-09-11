using CodexSwitcher.App.Services;
using CodexSwitcher.Core.Abstractions;
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
