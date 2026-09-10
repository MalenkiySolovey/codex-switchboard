using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexSwitcher.Infra.Codex;

/// <summary>
/// Asynchronous client communicating with a local <c>codex app-server --listen stdio://</c> process via JSON-RPC 2.0 (NDJSON).
/// </summary>
public interface ICodexAppServerClient : IAsyncDisposable, IDisposable
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
    Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
    Task StopAsync();
}

/// <summary>
/// Production implementation of <see cref="ICodexAppServerClient"/> that launches <c>codex app-server</c>
/// with an isolated <c>CODEX_HOME</c> environment.
/// </summary>
public sealed class CodexAppServerClient : ICodexAppServerClient
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ProcessStartInfo _startInfo;
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;

    private readonly object _pendingLock = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _nextId;
    private bool _disposed;
    private bool _stopping;

    public bool IsRunning => _process is not null && !_process.HasExited;

    public CodexAppServerClient(string codexExecutablePath, string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);

        _startInfo = new ProcessStartInfo
        {
            FileName = codexExecutablePath,
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

        _startInfo.ArgumentList.Add("app-server");
        _startInfo.ArgumentList.Add("--listen");
        _startInfo.ArgumentList.Add("stdio://");
        _startInfo.Environment["CODEX_HOME"] = codexHome;
        _startInfo.Environment.Remove("OPENAI_API_KEY");
        _startInfo.Environment.Remove("CODEX_API_KEY");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
            throw new InvalidOperationException("Client has already been started.");

        _process = new Process { StartInfo = _startInfo };
        _process.Start();

        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _stderr = _process.StandardError;

        _ = Task.Run(ReadLoopAsync, CancellationToken.None);
        _ = Task.Run(DrainStderrAsync, CancellationToken.None);

        try
        {
            // Initial handshake per app-server protocol:
            // 1. initialize request
            await RequestAsync("initialize",
                new { clientInfo = new { name = "CodexSwitchboard", title = "Codex Switchboard", version = "1.0.0" } },
                TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);

            // 2. initialized notification
            await NotifyAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> RequestAsync(
        string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
            throw new InvalidOperationException("app-server is not running.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingLock)
        {
            _pending[id] = tcs;
        }

        var message = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
            message["params"] = parameters;

        try
        {
            await WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
            var limit = timeout ?? TimeSpan.FromSeconds(20);
            return await tcs.Task.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"RPC request timed out waiting for '{method}'.");
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
        }
    }

    public async Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
            throw new InvalidOperationException("app-server is not running.");

        var message = new Dictionary<string, object?>
        {
            ["method"] = method,
        };
        if (parameters is not null)
            message["params"] = parameters;

        await WriteMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        if (_stdin is null)
            throw new InvalidOperationException("Standard input is closed.");

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
            if (_stdout is null) return;
            string? line;
            while ((line = await _stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                try
                {
                    HandleLine(line);
                }
                catch (JsonException)
                {
                    // Ignore non-JSON stray log lines
                }
            }
        }
        catch (Exception)
        {
            // Process exit or pipe closed
        }
        finally
        {
            if (!_stopping)
            {
                FailAllPending(new IOException("app-server stdout stream was closed."));
            }
        }
    }

    private void HandleLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        // Responses have "id" and ("result" or "error")
        if (root.TryGetProperty("id", out var idEl) && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)))
        {
            if (idEl.ValueKind != JsonValueKind.Number || !idEl.TryGetInt64(out var id))
                return;

            TaskCompletionSource<JsonElement>? tcs;
            lock (_pendingLock)
            {
                _pending.Remove(id, out tcs);
            }
            if (tcs is null) return;

            if (root.TryGetProperty("error", out var errEl))
            {
                string message = "Unknown error";
                if (errEl.ValueKind == JsonValueKind.Object && errEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
                {
                    message = msgEl.GetString() ?? "Unknown error";
                }
                tcs.TrySetException(new InvalidOperationException($"app-server error: {message}"));
            }
            else if (root.TryGetProperty("result", out var resEl))
            {
                tcs.TrySetResult(resEl.Clone());
            }
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
        foreach (var tcs in pending)
            tcs.TrySetException(ex);
    }

    private async Task DrainStderrAsync()
    {
        try
        {
            if (_stderr is null) return;
            while (await _stderr.ReadLineAsync().ConfigureAwait(false) is not null)
            {
                // Drain and discard stderr to avoid blocking process
            }
        }
        catch
        {
            // Process terminating
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        FailAllPending(new OperationCanceledException("app-server is stopping."));

        try { _stdin?.Close(); } catch { }

        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch { }
        }

        try { _process?.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        FailAllPending(new ObjectDisposedException(nameof(CodexAppServerClient)));

        try { _stdin?.Close(); } catch { }
        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        try { _process?.Dispose(); } catch { }
        _writeLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        Dispose();
    }
}
