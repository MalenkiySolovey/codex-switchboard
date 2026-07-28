namespace CodexSwitcher.Core.Models;

/// <summary>Resultado de uma transação de switch. Ver BUSINESS_RULES.md §4.</summary>
public sealed record SwitchResult(
    SwitchOutcome Outcome,
    string Message,
    ErrorInfo? Error = null,
    IReadOnlyList<CodexProcessInfo>? ClosedProcesses = null,
    IReadOnlyList<CodexProcessInfo>? ReopenFailures = null)
{
    public bool CredentialSwitched =>
        Outcome is SwitchOutcome.Success or SwitchOutcome.SuccessWithReopenWarning;
}

/// <summary>
/// Plano de um switch, exibido no popup de confirmação (§4.2). Descreve o que será
/// fechado e reaberto ANTES de qualquer ação destrutiva.
/// </summary>
public sealed record SwitchPlan(
    ProfileMetadata? FromProfile,
    ProfileMetadata ToProfile,
    IReadOnlyList<CodexProcessInfo> ToClose,
    IReadOnlyList<CodexProcessInfo> ToReopen,
    bool HasActiveCliWork)
{
    public IReadOnlyList<CodexProcessInfo> CliToClose =>
        ToClose.Where(p => p.Kind == CodexProcessKind.Cli).ToList();

    public IReadOnlyList<CodexProcessInfo> DesktopToClose =>
        ToClose.Where(p => p.Kind == CodexProcessKind.DesktopApp).ToList();
}

/// <summary>Resultado da reconciliação do slot ativo com os perfis conhecidos. Ver §4.6.</summary>
public sealed record ReconciliationResult(
    ActiveMatch Match,
    Guid? ActiveProfileId,
    string? ActiveSub,
    string? ActiveFingerprint)
{
    public static ReconciliationResult NoActive() =>
        new(ActiveMatch.None, null, null, null);
}
