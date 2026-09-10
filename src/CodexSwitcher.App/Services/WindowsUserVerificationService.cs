using CodexSwitcher.Core.Abstractions;
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
