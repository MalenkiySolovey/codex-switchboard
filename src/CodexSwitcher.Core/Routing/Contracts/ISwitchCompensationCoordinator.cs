using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Coordinates multi-resource rollback actions across config files, processes, and audit logs.
/// Executes compensations in reverse order of mutation upon transaction failure.
/// </summary>
public interface ISwitchCompensationCoordinator
{
    /// <summary>
    /// Registers the original bytes of config.toml prior to mutation.
    /// </summary>
    void RegisterConfigBackup(byte[] originalBytes);

    /// <summary>
    /// Registers processes captured prior to closing for relaunch upon completion or compensation.
    /// </summary>
    void RegisterCapturedProcesses(IReadOnlyList<CodexProcessInfo> capturedProcesses);

    /// <summary>
    /// Relaunches distinct captured desktop processes, collecting any relaunch failures.
    /// </summary>
    void ReopenDesktop(IReadOnlyList<CodexProcessInfo> captured, out List<CodexProcessInfo> failures);

    /// <summary>
    /// Executes registered compensations in reverse order (LIFO).
    /// </summary>
    Task CompensateAsync(string action, string reason, CancellationToken cancellationToken = default);
}
