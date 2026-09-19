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
using System.Diagnostics;
using System.Text;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>
/// Executa o binário <c>codex</c> resolvido do PATH (ou de um override), definindo
/// <c>CODEX_HOME</c> <b>apenas</b> no processo filho — nunca globalmente. Ver §5, §6 e ponto 7.
/// Suporta os wrappers do npm no Windows (codex.exe / codex.cmd / codex.ps1).
/// </summary>
public sealed class CodexCliRunner : ICodexCli
{
    private enum Launcher { Executable, Cmd, PowerShell }

    internal static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _resolvedPath;
    private readonly Launcher _launcher;
    private readonly ISwitchboardCodexProcessRegistry? _registry;

    public CodexCliRunner(string? executableOverride)
        : this(executableOverride, null)
    {
    }

    public CodexCliRunner(string? executableOverride = null, ISwitchboardCodexProcessRegistry? registry = null)
    {
        _resolvedPath = ResolveCodexPath(executableOverride);
        _launcher = _resolvedPath is null ? Launcher.Executable : LauncherFor(_resolvedPath);
        _registry = registry;
    }

    public bool IsAvailable => _resolvedPath is not null;
    public string? ResolvedPath => _resolvedPath;

    public async Task<ICodexLoginSession> StartChatGptLoginAsync(
        string codexHome, CancellationToken cancellationToken = default)
    {
        if (_resolvedPath is null)
            throw new InvalidOperationException("codex não encontrado no PATH");

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = codexHome,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom,
        };
        ApplyLauncher(psi);
        psi.ArgumentList.Add("app-server");
        // CODEX_HOME somente no filho (ponto 7); o app-server escreve o auth.json aqui ao concluir.
        psi.Environment["CODEX_HOME"] = codexHome;

        return await CodexAppServerLoginSession.StartAsync(psi, codexHome, cancellationToken, _registry)
            .ConfigureAwait(false);
    }

    /// <summary>Define FileName + argumentos iniciais conforme o wrapper (exe/cmd/ps1) do codex.</summary>
    private void ApplyLauncher(ProcessStartInfo psi)
    {
        switch (_launcher)
        {
            case Launcher.Cmd:
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(_resolvedPath!);
                break;
            case Launcher.PowerShell:
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(_resolvedPath!);
                break;
            default:
                psi.FileName = _resolvedPath!;
                break;
        }
    }

    private async Task<CodexCliResult> RunAsync(
        IReadOnlyList<string> codexArgs, string codexHome, TimeSpan timeout,
        Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        if (_resolvedPath is null)
            return new CodexCliResult(-1, string.Empty, "codex não encontrado no PATH");

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = codexHome,
        };

        ApplyLauncher(psi);

        foreach (var a in codexArgs)
            psi.ArgumentList.Add(a);

        // CODEX_HOME somente no filho (ponto 7).
        psi.Environment["CODEX_HOME"] = codexHome;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        int processId = 0;
        try
        {
            process.Start();
            processId = process.Id;
            _registry?.RegisterOwnedProcess(processId);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new CodexCliResult(-2, stdout.ToString(), "tempo esgotado ao executar codex");
            }

            return new CodexCliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CodexCliResult(-1, string.Empty, "falha ao iniciar o processo codex");
        }
        finally
        {
            if (processId > 0)
                _registry?.UnregisterOwnedProcess(processId);
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static Launcher LauncherFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cmd" or ".bat" => Launcher.Cmd,
            ".ps1" => Launcher.PowerShell,
            _ => Launcher.Executable,
        };

    public static string? ResolveCodexPath(string? overridePath = null)
    {
        var runtime = new CodexRuntimeResolver().ResolveCurrentRuntime(overridePath);
        return string.IsNullOrEmpty(runtime.ExecutablePath) ? null : runtime.ExecutablePath;
    }
}
