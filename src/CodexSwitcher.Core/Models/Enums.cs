namespace CodexSwitcher.Core.Models;

/// <summary>
/// Saúde da credencial de um perfil. Ver BUSINESS_RULES.md §3.2.
/// Ortogonal à ocupação do slot ativo (<see cref="ProfileMetadata.IsActive"/>).
/// </summary>
public enum HealthStatus
{
    /// <summary>Ainda não avaliado, ou blob não decifrável (perfil de outra máquina/usuário).</summary>
    Unknown = 0,

    /// <summary>Credencial presente no cofre.</summary>
    Valid = 1,

    /// <summary>Credencial inválida/expirada; requer novo login OAuth.</summary>
    NeedsReLogin = 2,

    /// <summary>Falha transiente/ambiental (rede, codex ausente, permissão, timeout).</summary>
    Error = 3,
}

/// <summary>Categoria de erro para mensagens específicas na UI. Nunca contém tokens.</summary>
public enum ErrorCategory
{
    None = 0,
    Network,
    RefreshTokenExpired,
    CodexNotFound,
    PermissionDenied,
    Timeout,
    KeyringStorage,
    DecryptionFailed,
    PortInUse,
    ProcessRemnant,
    InvalidAuthFile,
    Unknown,
}

/// <summary>Classificação de um processo do Codex encontrado. Ver BUSINESS_RULES.md §4.5.</summary>
public enum CodexProcessKind
{
    /// <summary>Não classificado com confiança — não deve ser encerrado.</summary>
    Unknown = 0,

    /// <summary>App desktop do Codex — fechável e reabrível.</summary>
    DesktopApp = 1,

    /// <summary>CLI codex/codex.exe — fechável, NÃO reabrível.</summary>
    Cli = 2,

    /// <summary>Processo do editor que hospeda a extensão — NUNCA fechar.</summary>
    IdeHost = 3,
}

/// <summary>Resultado global de uma transação de switch. Ver BUSINESS_RULES.md §4.3/§4.4.</summary>
public enum SwitchOutcome
{
    /// <summary>Troca concluída e apps reabertos.</summary>
    Success = 0,

    /// <summary>Troca concluída, mas a reabertura de algum app falhou (credencial já trocada).</summary>
    SuccessWithReopenWarning = 1,

    /// <summary>Usuário cancelou no popup de confirmação; nada foi alterado.</summary>
    Cancelled = 2,

    /// <summary>Abortada por processo codex remanescente antes de tocar no slot; nada alterado.</summary>
    AbortedProcessRemnant = 3,

    /// <summary>Falha na troca; slot ativo restaurado do backup e apps reabertos na conta original.</summary>
    RolledBack = 4,

    /// <summary>Falha sem alteração do slot (ex.: perfil alvo não decifrável).</summary>
    Failed = 5,
}

/// <summary>Como o app deve tratar o fechar/reabrir de apps do Codex no switch. Ver §4.5.</summary>
public enum CloseReopenMode
{
    /// <summary>Fechar antes e reabrir depois automaticamente (sempre com confirmação).</summary>
    Automatic = 0,

    /// <summary>Apenas avisar quais apps estão abertos; não fechar/reabrir.</summary>
    WarnOnly = 1,

    /// <summary>Não fazer nada com processos.</summary>
    DoNothing = 2,
}

/// <summary>Como o cofre casou o slot ativo com um perfil conhecido. Ver §4.6.</summary>
public enum ActiveMatch
{
    /// <summary>Nenhum perfil casa; conta não gerenciada (ou slot vazio).</summary>
    None = 0,

    /// <summary>Fingerprint idêntico: perfil está exatamente igual ao slot ativo.</summary>
    Exact = 1,

    /// <summary>Mesma conta (sub) mas conteúdo diferente: Codex renovou externamente; requer write-back.</summary>
    SameAccountDrifted = 2,
}
