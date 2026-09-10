namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Resultado da tentativa de autorização para revelar códigos TOTP.
/// </summary>
public sealed record TotpAuthorizationOutcome(bool Success, WindowsVerificationResult Status);

/// <summary>
/// Gerencia a sessão de autorização em memória para revelação de códigos 2FA.
/// Nunca persiste o estado da sessão em disco.
/// Não possui conhecimento de segredos TOTP, quotas ou contas.
/// </summary>
public interface ITotpRevealAuthorizationService
{
    /// <summary>Indica se a política de proteção por verificação do Windows está ativada nas preferências.</summary>
    bool IsProtectionEnabled { get; }

    /// <summary>Indica se há uma sessão de autorização válida e ativa neste momento.</summary>
    bool IsAuthorized { get; }

    /// <summary>Tempo restante da sessão de autorização atual (TimeSpan.Zero se expirada ou bloqueada).</summary>
    TimeSpan RemainingDuration { get; }

    /// <summary>Indica se uma verificação do Windows é necessária para permitir a revelação.</summary>
    bool IsVerificationRequired();

    /// <summary>
    /// Garante que a autorização esteja ativa, disparando a verificação do Windows se necessário.
    /// Chamadas simultâneas são agrupadas para evitar múltiplos diálogos concorrentes.
    /// </summary>
    Task<TotpAuthorizationOutcome> EnsureAuthorizedAsync(string? message = null);

    /// <summary>Invalida a sessão de autorização imediatamente, retornando ao estado BLOQUEADO.</summary>
    void Invalidate();

    /// <summary>Notifica que configurações de segurança foram alteradas, invalidando qualquer sessão ativa.</summary>
    void RecordSettingsChanged();
}
