namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Estado efetivo da proteção por verificação do Windows para revelação de códigos 2FA.
/// </summary>
public enum EffectiveTotpProtectionState
{
    /// <summary>Desativada pelo usuário nas configurações.</summary>
    DisabledByUser,

    /// <summary>Ativada e com verificação do Windows operacional (Hello/PIN ou senha da conta disponíveis).</summary>
    Ready,

    /// <summary>Ativada, porém nenhum método de verificação está disponível (Hello e senha indisponíveis).</summary>
    DegradedUnavailable,

    /// <summary>Ativada, mas temporariamente indisponível (dispositivo ocupado ou erro transitório).</summary>
    TemporarilyUnavailable
}

/// <summary>
/// Ação resultante da avaliação de autorização para revelação do código TOTP.
/// </summary>
public enum TotpAuthorizationAction
{
    /// <summary>Autorizado: verificação do Windows bem-sucedida ou sessão ativa.</summary>
    Authorized,

    /// <summary>Degradação controlada: proteção ativada, mas verificação indisponível; permite revelação única de emergência com auto-ocultação de 10s.</summary>
    DegradedFallback,

    /// <summary>Temporariamente indisponível: dispositivo ocupado ou erro transitório de infraestrutura; requer decisão do usuário.</summary>
    TemporarilyUnavailable,

    /// <summary>Cancelado pelo usuário no diálogo do Windows; revelação bloqueada.</summary>
    Canceled,

    /// <summary>Falha de autenticação (senha incorreta, falha de Hello ou usuário diferente); revelação bloqueada.</summary>
    Failed
}

/// <summary>
/// Status detalhado dos métodos de verificação do Windows para exibição na UI de configurações.
/// </summary>
public sealed record TotpVerificationMethodsStatus(
    bool IsProtectionEnabled,
    WindowsVerificationAvailability HelloAvailability,
    bool IsPasswordSupported,
    EffectiveTotpProtectionState ProtectionState);

/// <summary>
/// Resultado da tentativa de autorização para revelar códigos TOTP.
/// </summary>
public sealed record TotpAuthorizationOutcome(
    bool Success,
    WindowsVerificationResult Status,
    TotpAuthorizationAction Action = TotpAuthorizationAction.Authorized,
    WindowsVerificationAvailability Availability = WindowsVerificationAvailability.Available,
    WindowsPasswordVerificationResult? PasswordStatus = null)
{
    /// <summary>Indica se o código TOTP pode ser revelado com segurança.</summary>
    public bool CanReveal => Success;

    /// <summary>Indica se a revelação ocorreu por fallback de emergência (sem iniciar sessão de autorização).</summary>
    public bool IsDegradedFallback => Action == TotpAuthorizationAction.DegradedFallback;

    /// <summary>Indica se a verificação falhou temporariamente por erro de infraestrutura.</summary>
    public bool IsTemporarilyUnavailable => Action == TotpAuthorizationAction.TemporarilyUnavailable;
}

/// <summary>
/// Gerencia a sessão de autorização em memória para revelação de códigos 2FA.
/// O estado de autorização nunca é gravado em disco.
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
    /// Garante que a autorização esteja ativa, disparando a verificação do Windows (Hello ou senha) se necessário.
    /// Chamadas simultâneas são agrupadas para evitar múltiplos diálogos concorrentes.
    /// </summary>
    Task<TotpAuthorizationOutcome> EnsureAuthorizedAsync(string? message = null);

    /// <summary>Avalia e retorna o estado efetivo da proteção no momento.</summary>
    Task<EffectiveTotpProtectionState> GetEffectiveProtectionStateAsync();

    /// <summary>Retorna o status consolidado de disponibilidade dos métodos de verificação (Hello e senha).</summary>
    Task<TotpVerificationMethodsStatus> GetVerificationMethodsStatusAsync();

    /// <summary>
    /// Valida a senha da conta Windows do usuário atual especificamente para autorizar a desativação da proteção.
    /// Exige validação de senha mesmo que haja uma sessão de autorização ativa em memória.
    /// Nunca acessa ou decodifica segredos TOTP.
    /// </summary>
    Task<bool> VerifyPasswordToDisableProtectionAsync(string? message = null);

    /// <summary>Invalida a sessão de autorização imediatamente, retornando ao estado BLOQUEADO.</summary>
    void Invalidate();

    /// <summary>Notifica que configurações de segurança foram alteradas, invalidando qualquer sessão ativa.</summary>
    void RecordSettingsChanged();
}
