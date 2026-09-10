using System.Diagnostics;
using System.Text;
using CodexSwitcher.Core.Abstractions;

namespace CodexSwitcher.Infra.Codex;

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

    public CodexCliRunner(string? executableOverride = null)
    {
        _resolvedPath = ResolveCodexPath(executableOverride);
        _launcher = _resolvedPath is null ? Launcher.Executable : LauncherFor(_resolvedPath);
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

        return await CodexAppServerLoginSession.StartAsync(psi, codexHome, cancellationToken)
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

        try
        {
            process.Start();
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
