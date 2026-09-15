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
using Windows.Security.Credentials.UI;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Implementação Windows Desktop da verificação de identidade do usuário via UserConsentVerifier / UserConsentVerifierInterop.
/// Associa o HWND da janela principal para garantir foco correto no diálogo de verificação.
/// Nunca solicita, recebe ou manipula credenciais/senhas brutas do Windows.
/// </summary>
public sealed class WindowsUserVerificationService : IWindowsUserVerificationService
{
    private readonly IWindowHandleProvider _windowHandleProvider;

    public WindowsUserVerificationService(IWindowHandleProvider windowHandleProvider)
    {
        _windowHandleProvider = windowHandleProvider ?? throw new ArgumentNullException(nameof(windowHandleProvider));
    }

    public async Task<WindowsVerificationAvailability> CheckAvailabilityAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability switch
            {
                UserConsentVerifierAvailability.Available => WindowsVerificationAvailability.Available,
                UserConsentVerifierAvailability.DeviceNotPresent => WindowsVerificationAvailability.DeviceNotPresent,
                UserConsentVerifierAvailability.NotConfiguredForUser => WindowsVerificationAvailability.NotConfiguredForUser,
                UserConsentVerifierAvailability.DisabledByPolicy => WindowsVerificationAvailability.DisabledByPolicy,
                UserConsentVerifierAvailability.DeviceBusy => WindowsVerificationAvailability.DeviceBusy,
                _ => WindowsVerificationAvailability.Unknown,
            };
        }
        catch (PlatformNotSupportedException)
        {
            return WindowsVerificationAvailability.UnsupportedOperatingSystem;
        }
        catch (TypeLoadException)
        {
            return WindowsVerificationAvailability.UnsupportedOperatingSystem;
        }
        catch (Exception)
        {
            return WindowsVerificationAvailability.Unknown;
        }
    }

    public async Task<WindowsVerificationResult> RequestVerificationAsync(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        try
        {
            var hwnd = _windowHandleProvider.MainWindowHandle;
            UserConsentVerificationResult result;

            if (hwnd != IntPtr.Zero)
            {
                result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(hwnd, message);
            }
            else
            {
                result = await UserConsentVerifier.RequestVerificationAsync(message);
            }

            return result switch
            {
                UserConsentVerificationResult.Verified => WindowsVerificationResult.Verified,
                UserConsentVerificationResult.Canceled => WindowsVerificationResult.Canceled,
                UserConsentVerificationResult.DeviceNotPresent => WindowsVerificationResult.NotAvailable,
                UserConsentVerificationResult.NotConfiguredForUser => WindowsVerificationResult.NotConfigured,
                UserConsentVerificationResult.DisabledByPolicy => WindowsVerificationResult.DisabledByPolicy,
                UserConsentVerificationResult.DeviceBusy => WindowsVerificationResult.DeviceBusy,
                UserConsentVerificationResult.RetriesExhausted => WindowsVerificationResult.RetriesExhausted,
                _ => WindowsVerificationResult.Failed,
            };
        }
        catch (PlatformNotSupportedException)
        {
            return WindowsVerificationResult.UnsupportedOperatingSystem;
        }
        catch (TypeLoadException)
        {
            return WindowsVerificationResult.UnsupportedOperatingSystem;
        }
        catch (Exception)
        {
            return WindowsVerificationResult.Failed;
        }
    }
}
