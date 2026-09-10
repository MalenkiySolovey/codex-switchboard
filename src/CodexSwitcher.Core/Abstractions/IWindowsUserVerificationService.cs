namespace CodexSwitcher.Core.Abstractions;

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
