using System.Collections.Concurrent;
using System.Diagnostics;
using CodexSwitcher.Core.Routing.Contracts;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>
/// Thread-safe registry tracking child Codex app-server and CLI processes spawned by Codex Switchboard.
/// Ensures child processes are not mistaken for external user CLI instances, and guarantees prompt,
/// bounded parallel termination upon application shutdown.
/// </summary>
public sealed class SwitchboardCodexProcessRegistry : ISwitchboardCodexProcessRegistry
{
    private readonly ConcurrentDictionary<int, byte> _ownedPids = new();

    public void RegisterOwnedProcess(int processId)
    {
        if (processId > 0)
        {
            _ownedPids.TryAdd(processId, 0);
        }
    }

    public void UnregisterOwnedProcess(int processId)
    {
        _ownedPids.TryRemove(processId, out _);
    }

    public bool IsOwnedProcess(int processId) =>
        processId > 0 && _ownedPids.ContainsKey(processId);

    public IReadOnlyCollection<int> GetOwnedProcessIds() =>
        _ownedPids.Keys.ToArray();

    public async Task TerminateAllOwnedProcessesAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var pids = GetOwnedProcessIds();
        if (pids.Count == 0)
            return;

        var tasks = pids.Select(pid => TerminateSingleProcessAsync(pid, timeout, cancellationToken)).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task TerminateSingleProcessAsync(int pid, TimeSpan totalTimeout, CancellationToken cancellationToken)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited)
            {
                UnregisterOwnedProcess(pid);
                return;
            }

            try
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                    proc.CloseMainWindow();
            }
            catch { }

            var gracePeriod = TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(50, totalTimeout.TotalMilliseconds / 3)));
            try
            {
                using var graceCts = new CancellationTokenSource(gracePeriod);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(graceCts.Token, cancellationToken);
                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            if (!proc.HasExited)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    using var killTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    using var killLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(killTimeoutCts.Token, cancellationToken);
                    await proc.WaitForExitAsync(killLinkedCts.Token).ConfigureAwait(false);
                }
                catch { }
            }
        }
        catch (ArgumentException) { /* Process already exited */ }
        catch (InvalidOperationException) { /* Process already exited */ }
        catch { }
        finally
        {
            UnregisterOwnedProcess(pid);
        }
    }
}
