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
namespace CodexSwitcher.Core.Security.Verification;

/// <summary>
/// Status de disponibilidade da verificação de identidade do Windows (Hello/PIN/biometria).
/// </summary>
public enum WindowsVerificationAvailability
{
    Available,
    DeviceNotPresent,
    NotConfiguredForUser,
    DisabledByPolicy,
    DeviceBusy,
    UnsupportedOperatingSystem,
    Unknown
}

/// <summary>
/// Resultado da operação de verificação de identidade do usuário pelo Windows.
/// </summary>
public enum WindowsVerificationResult
{
    Verified,
    Canceled,
    NotAvailable,
    NotConfigured,
    DisabledByPolicy,
    DeviceBusy,
    RetriesExhausted,
    UnsupportedOperatingSystem,
    Failed
}

/// <summary>
/// Abstração para verificar a identidade do usuário atual via Windows (Hello, PIN, biometria).
/// Nunca coleta, recebe ou armazena senhas do Windows.
/// </summary>
public interface IWindowsUserVerificationService
{
    /// <summary>
    /// Consulta se a verificação de consentimento do usuário está disponível no sistema operacional.
    /// </summary>
    Task<WindowsVerificationAvailability> CheckAvailabilityAsync();

    /// <summary>
    /// Solicita ao Windows a verificação do usuário atual com a mensagem explicativa fornecida.
    /// </summary>
    Task<WindowsVerificationResult> RequestVerificationAsync(string message);
}
