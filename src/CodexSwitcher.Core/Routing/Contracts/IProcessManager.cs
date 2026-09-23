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
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Descoberta e controle de processos do Codex do usuário atual. Ver BUSINESS_RULES.md §4.5
/// e pontos 19–27. Só atua sobre processos do próprio usuário; nunca eleva privilégio.
/// </summary>
public interface IProcessManager
{
    /// <summary>Enumera processos do Codex em execução do usuário atual, já classificados.</summary>
    IReadOnlyList<CodexProcessInfo> FindRunningCodexProcesses();

    /// <summary>Existe alguma CLI codex viva? Usado na verificação anti-corrida (§4.3 passo 4).</summary>
    bool AnyCodexCliRunning();

    /// <summary>
    /// Fecha graciosamente (CloseMainWindow) e, após timeout, mata o que sobrar.
    /// Retorna a lista efetivamente encerrada. Só processos <see cref="CodexProcessInfo.IsClosable"/>.
    /// </summary>
    Task<IReadOnlyList<CodexProcessInfo>> CloseGracefullyThenKillAsync(
        IReadOnlyList<CodexProcessInfo> targets,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default);

    /// <summary>Relança um processo reabrível (só app desktop). Lança em falha para o chamador tratar.</summary>
    void Relaunch(CodexProcessInfo process);

    /// <summary>Tenta iniciar o aplicativo desktop do Codex se instalado.</summary>
    bool TryLaunchDesktop();
}
