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
using System.Diagnostics;
using System.Text.Json;
using CodexSwitcher.Core.Routing.Contracts;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>
/// Conduz um login OAuth (ChatGPT) falando o protocolo JSON-RPC (NDJSON por stdio) do
/// <c>codex app-server</c>. Fluxo: <c>initialize</c> → <c>initialized</c> → <c>account/login/start</c>
/// (devolve a <c>authUrl</c> + <c>loginId</c>). O cliente abre a URL no WebView2 limpo; o app-server
/// mantém o servidor de callback local (localhost:1455), faz a troca de tokens e escreve o
/// <c>auth.json</c> no CODEX_HOME isolado, sinalizando <c>account/login/completed</c>. Assim NÃO abrimos
/// o navegador do sistema (sessão visitante preservada) e NÃO dependemos do device-auth (que exige uma
/// configuração de segurança do ChatGPT). Ver BUSINESS_RULES.md §5.
/// </summary>
internal sealed class CodexAppServerLoginSession : ICodexLoginSession
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process _process;
    private readonly object _pendingLock = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource<CodexLoginResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private StreamWriter _stdin = null!;
    private StreamReader _stdout = null!;
    private StreamReader _stderr = null!;
    private long _nextId;
    private bool _disposed;

    public string AuthUrl { get; private set; } = string.Empty;
    public string LoginId { get; private set; } = string.Empty;
    public Task<CodexLoginResult> Completion => _completion.Task;

    private CodexAppServerLoginSession(Process process) => _process = process;

    public static async Task<CodexAppServerLoginSession> StartAsync(
        ProcessStartInfo psi, string codexHome, CancellationToken cancellationToken)
    {
        var process = new Process { StartInfo = psi };
        var session = new CodexAppServerLoginSession(process);

        process.Start();
        session._stdin = process.StandardInput;
        session._stdout = process.StandardOutput;
        session._stderr = process.StandardError;
        _ = Task.Run(session.ReadLoopAsync, CancellationToken.None);
        _ = Task.Run(session.DrainStderrAsync, CancellationToken.None);

        try
        {
            // Handshake do app-server. clientInfo.name vira o "originator" no OAuth (apenas telemetria).
            await session.SendRequestAsync("initialize",
                new { clientInfo = new { name = "CodexSwitcher", version = "1.0.0" } },
                TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            await session.SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);

            var result = await session.SendRequestAsync("account/login/start",
                new { type = "chatgpt" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

            session.AuthUrl = result.TryGetProperty("authUrl", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()!
                : throw new InvalidOperationException("app-server não retornou a URL de autorização");
            session.LoginId = result.TryGetProperty("loginId", out var lid) && lid.ValueKind == JsonValueKind.String
                ? lid.GetString()!
                : string.Empty;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return session;
    }

    private async Task<JsonElement> SendRequestAsync(
        string method, object? @params, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[id] = tcs;

        try
        {
            await WriteMessageAsync(BuildMessage(id, method, @params), cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"tempo esgotado aguardando resposta de '{method}' do codex app-server");
        }
        finally
        {
            lock (_pendingLock) _pending.Remove(id);
        }
    }

    private Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken) =>
        WriteMessageAsync(BuildMessage(null, method, @params), cancellationToken);

    private static Dictionary<string, object?> BuildMessage(long? id, string method, object? @params)
    {
        var msg = new Dictionary<string, object?> { ["method"] = method };
        if (id is not null) msg["id"] = id;
        if (@params is not null) msg["params"] = @params;
        return msg;
    }

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                try { HandleLine(line); }
                catch (JsonException) { /* linha não-JSON (log solto): ignorar */ }
            }
        }
        catch (Exception)
        {
            // stdout fechou (processo encerrando): tratado no finally.
        }
        finally
        {
            FailAllPending(new IOException("o codex app-server encerrou a saída"));
            _completion.TrySetResult(new CodexLoginResult(false,
                "o processo do codex encerrou antes de concluir o login"));
        }
    }

    private void HandleLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        var isResponse = root.TryGetProperty("id", out var idEl)
            && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _));

        if (isResponse)
        {
            if (idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt64(out var id)) return;

            TaskCompletionSource<JsonElement>? tcs;
            lock (_pendingLock) _pending.Remove(id, out tcs);
            if (tcs is null) return;

            if (root.TryGetProperty("error", out var err))
            {
                var message = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : err.ToString();
                tcs.TrySetException(new InvalidOperationException($"codex app-server: {message}"));
            }
            else
            {
                tcs.TrySetResult(root.GetProperty("result").Clone());
            }
            return;
        }

        if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String
            && methodEl.GetString() == "account/login/completed")
        {
            var success = root.TryGetProperty("params", out var p)
                && p.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            string? error = null;
            if (root.TryGetProperty("params", out var pp)
                && pp.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                error = e.GetString();
            }
            _completion.TrySetResult(new CodexLoginResult(success, error));
        }
    }

    private void FailAllPending(Exception ex)
    {
        List<TaskCompletionSource<JsonElement>> pending;
        lock (_pendingLock)
        {
            pending = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var tcs in pending) tcs.TrySetException(ex);
    }

    private async Task DrainStderrAsync()
    {
        try { while (await _stderr.ReadLineAsync().ConfigureAwait(false) is not null) { /* descarta */ } }
        catch (Exception) { /* processo encerrando */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancela o login em andamento (best-effort) antes de matar o processo.
        try
        {
            if (!string.IsNullOrEmpty(LoginId) && !_process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendNotificationAsync("account/login/cancel", new { loginId = LoginId }, cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception) { /* cancelamento é best-effort */ }

        try { _stdin.Close(); } catch (Exception) { }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (Exception) { }
        try { _process.Dispose(); } catch (Exception) { }

        _completion.TrySetResult(new CodexLoginResult(false, "login cancelado"));
        _writeLock.Dispose();
    }
}
