using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
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
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Errors;


namespace CodexSwitcher.Core.Routing.Models;

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
