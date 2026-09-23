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
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using System.Diagnostics;

namespace CodexSwitcher.Infra.Codex.Runtime;

using SysProcess = System.Diagnostics.Process;

/// <summary>
/// Descobre e controla processos do Codex do usuário/sessão atual. Classifica por caminho do
/// executável (curado) para evitar falsos positivos. Nunca toca editores/IDE. O app desktop é
/// empacotado (MSIX, em WindowsApps) e é reaberto diretamente pelo AppUserModelID registrado no
/// Windows — nunca por <c>codex app</c>, que pode invocar o bootstrapper/atualizador. Ver §4.5.
/// </summary>
public sealed class CodexProcessManager : IProcessManager
{
    private readonly ISwitchboardCodexProcessRegistry? _registry;

    public CodexProcessManager() : this(null)
    {
    }

    public CodexProcessManager(ISwitchboardCodexProcessRegistry? registry = null)
    {
        _registry = registry;
    }

    public IReadOnlyList<CodexProcessInfo> FindRunningCodexProcesses()
    {
        var currentSession = SafeSessionId(SysProcess.GetCurrentProcess());
        var found = new List<CodexProcessInfo>();

        // O pacote OpenAI.Codex atual usa ChatGPT.exe para o desktop app; a CLI é codex.exe.
        foreach (var processName in new[] { "ChatGPT", "codex" })
        {
            foreach (var proc in SysProcess.GetProcessesByName(processName))
            {
                try
                {
                    if (SafeSessionId(proc) != currentSession)
                        continue; // só a sessão atual (ponto 26)

                    if (_registry?.IsOwnedProcess(proc.Id) == true)
                        continue; // Processo filho interno do Switchboard (monitor app-server/login); ignorar para detecção de CLI do usuário

                    var path = SafeExecutablePath(proc);
                    if (path is null)
                        continue;

                    var kind = Classify(path.ToLowerInvariant());
                    if (kind == CodexProcessKind.Unknown)
                        continue;

                    found.Add(new CodexProcessInfo(proc.Id, proc.ProcessName, path, null, kind));
                }
                catch (InvalidOperationException) { /* processo terminou */ }
                finally { proc.Dispose(); }
            }
        }

        return found;
    }

    public bool AnyCodexCliRunning() =>
        FindRunningCodexProcesses().Any(p => p.Kind == CodexProcessKind.Cli);

    public async Task<IReadOnlyList<CodexProcessInfo>> CloseGracefullyThenKillAsync(
        IReadOnlyList<CodexProcessInfo> targets, TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        var closable = targets.Where(t => t.IsClosable).ToList();
        var live = new List<(CodexProcessInfo Info, SysProcess Proc)>();

        // (1) Fechamento gracioso: pedir para a janela fechar (dá chance de salvar).
        foreach (var t in closable)
        {
            var proc = TryGetProcess(t.Pid);
            if (proc is null) continue;
            try
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                    proc.CloseMainWindow();
                live.Add((t, proc));
            }
            catch (InvalidOperationException) { proc.Dispose(); }
        }

        // (2) Aguardar saída até o timeout.
        var deadline = DateTimeOffset.UtcNow + gracefulTimeout;
        var closed = new List<CodexProcessInfo>();
        while (live.Count > 0 && DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            for (var i = live.Count - 1; i >= 0; i--)
            {
                if (live[i].Proc.HasExited)
                {
                    closed.Add(live[i].Info);
                    live[i].Proc.Dispose();
                    live.RemoveAt(i);
                }
            }
            if (live.Count > 0)
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        // (3) Forçar (Kill) o que sobrou.
        foreach (var (info, proc) in live)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
                closed.Add(info);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { proc.Dispose(); }
        }

        return closed;
    }

    public void Relaunch(CodexProcessInfo process)
    {
        if (process.Kind != CodexProcessKind.DesktopApp)
            throw new InvalidOperationException("Apenas o app desktop é reabrível.");

        var appUserModelId = TryGetAppUserModelId(process.ExecutablePath);
        if (appUserModelId is null)
            throw new InvalidOperationException("Não foi possível identificar o pacote instalado do Codex.");

        // shell:AppsFolder ativa o app MSIX registrado. Não executa o binário protegido de
        // WindowsApps diretamente e não chama `codex app`, portanto não dispara instalador.
        var psi = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true,
        };
        psi.ArgumentList.Add($"shell:AppsFolder\\{appUserModelId}");
        Process.Start(psi);
    }

    public bool TryLaunchDesktop()
    {
        var running = FindRunningCodexProcesses().FirstOrDefault(p => p.Kind == CodexProcessKind.DesktopApp);
        if (running != null)
        {
            try
            {
                Relaunch(running);
                return true;
            }
            catch
            {
                // Fall through to standard AppUserModelId
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            };
            psi.ArgumentList.Add("shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App");
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryOpenThreadDeepLink(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        try
        {
            var deepLink = $"codex://threads/{Uri.EscapeDataString(threadId)}";
            Process.Start(new ProcessStartInfo
            {
                FileName = deepLink,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static CodexProcessKind Classify(string lowerPath)
    {
        // App desktop empacotado (MSIX): ...\WindowsApps\OpenAI.Codex_<ver>_..\app\ChatGPT.exe.
        if (lowerPath.Contains("openai.codex") || lowerPath.Contains(@"\windowsapps\openai"))
            return CodexProcessKind.DesktopApp;

        // CLI standalone (npm global ou binário em AppData\Local\OpenAI\Codex\bin).
        if (lowerPath.Contains(@"\npm\")
            || lowerPath.Contains(@"\openai\codex\bin")
            || lowerPath.Contains(@"node_modules\@openai\codex"))
            return CodexProcessKind.Cli;

        return CodexProcessKind.Unknown;
    }

    /// <summary>
    /// Converte o diretório de instalação MSIX em AppUserModelID. Ex.:
    /// OpenAI.Codex_26.721.4979.0_x64__2p2nqsd0c76g0 -> OpenAI.Codex_2p2nqsd0c76g0!App.
    /// </summary>
    private static string? TryGetAppUserModelId(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;

        var packageDirectory = Directory.GetParent(executablePath)?.Parent?.Name;
        if (string.IsNullOrWhiteSpace(packageDirectory)) return null;

        var separator = packageDirectory.LastIndexOf("__", StringComparison.Ordinal);
        var firstUnderscore = packageDirectory.IndexOf('_');
        if (separator <= 0 || firstUnderscore <= 0 || separator + 2 >= packageDirectory.Length)
            return null;

        var packageName = packageDirectory[..firstUnderscore];
        var publisherId = packageDirectory[(separator + 2)..];
        return $"{packageName}_{publisherId}!App";
    }

    private static SysProcess? TryGetProcess(int pid)
    {
        try { return SysProcess.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string? SafeExecutablePath(SysProcess proc)
    {
        try { return proc.MainModule?.FileName; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null; // acesso negado (outro usuário) ou processo protegido → ignorar
        }
    }

    private static int SafeSessionId(SysProcess proc)
    {
        try { return proc.SessionId; }
        catch (InvalidOperationException) { return -1; }
    }
}
