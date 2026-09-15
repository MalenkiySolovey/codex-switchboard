
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
namespace CodexSwitcher.Core.Routing.Models;

/// <summary>
/// Instantâneo de um processo do Codex em execução, capturado ANTES de encerrar,
/// para permitir a reabertura. Ver BUSINESS_RULES.md §4.5 e ponto 23.
/// </summary>
public sealed record CodexProcessInfo(
    int Pid,
    string ProcessName,
    string? ExecutablePath,
    string? Arguments,
    CodexProcessKind Kind)
{
    /// <summary>Só o app desktop é reabrível; CLI não; IDE nunca é tocada.</summary>
    public bool IsReopenable => Kind == CodexProcessKind.DesktopApp && !string.IsNullOrEmpty(ExecutablePath);

    /// <summary>Deve ser encerrado num switch? IDE host nunca; desconhecido nunca.</summary>
    public bool IsClosable => Kind is CodexProcessKind.DesktopApp or CodexProcessKind.Cli;
}
