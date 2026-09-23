using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Routing.Services;

/// <summary>
/// Production implementation of <see cref="ISwitchCompensationCoordinator"/> providing
/// reverse-order multi-resource compensation for target switch transactions.
/// </summary>
public sealed class SwitchCompensationCoordinator : ISwitchCompensationCoordinator
{
    private readonly ICodexRoutingConfigStore _routingConfig;
    private readonly IProcessManager _processes;
    private readonly IAuditLog _audit;
    private readonly CodexPaths _paths;

    private byte[]? _configOriginalBytes;
    private IReadOnlyList<CodexProcessInfo> _capturedProcesses = [];

    public SwitchCompensationCoordinator(
        ICodexRoutingConfigStore routingConfig,
        IProcessManager processes,
        IAuditLog audit,
        CodexPaths paths)
    {
        _routingConfig = routingConfig ?? throw new ArgumentNullException(nameof(routingConfig));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public void RegisterConfigBackup(byte[] originalBytes)
    {
        _configOriginalBytes = originalBytes ?? throw new ArgumentNullException(nameof(originalBytes));
    }

    public void RegisterCapturedProcesses(IReadOnlyList<CodexProcessInfo> capturedProcesses)
    {
        _capturedProcesses = capturedProcesses ?? throw new ArgumentNullException(nameof(capturedProcesses));
    }

    public void ReopenDesktop(IReadOnlyList<CodexProcessInfo> captured, out List<CodexProcessInfo> failures)
    {
        failures = [];
        var distinct = captured
            .Where(p => p.IsReopenable)
            .GroupBy(p => p.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        foreach (var p in distinct)
        {
            try
            {
                _processes.Relaunch(p);
            }
            catch (Exception)
            {
                failures.Add(p);
            }
        }

        // Safe Human-QA Diagnostic Log (no prompt text, no credentials, no auth, no thread content)
        _audit.Record(
            "desktop-reopen-diagnostic",
            failures.Count == 0 ? "success" : "partial",
            $"DESKTOP_STOP_RESULT=Stopped({captured.Count}) DESKTOP_LAUNCH_RESULT={(failures.Count == 0 ? "Success" : $"Failures({failures.Count})")}");
        System.Diagnostics.Trace.TraceInformation($"[DesktopProcessDiagnostic] DESKTOP_STOP_RESULT=Stopped({captured.Count}) DESKTOP_LAUNCH_RESULT={(failures.Count == 0 ? "Success" : $"Failures({failures.Count})")}");
    }

    public Task CompensateAsync(string action, string reason, CancellationToken cancellationToken = default)
    {
        // 1. Restore config.toml exact bytes if registered
        if (_configOriginalBytes is not null)
        {
            try
            {
                _routingConfig.RestoreExactBytes(_paths.ConfigTomlPath, _configOriginalBytes);
            }
            catch (Exception ex)
            {
                _audit.Record("switch-compensation", "failed", $"config restore error: {ex.Message}");
            }
        }

        // 2. Reopen captured desktop applications if any were closed
        if (_capturedProcesses.Count > 0)
        {
            ReopenDesktop(_capturedProcesses, out _);
        }

        // 3. Record compensation audit entry
        _audit.Record(action, "rolled-back", reason);
        return Task.CompletedTask;
    }
}
