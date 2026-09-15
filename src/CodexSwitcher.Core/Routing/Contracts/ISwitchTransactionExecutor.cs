using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Executes validated <see cref="CodexSwitchPlan"/> transactions.
/// Coordinates process closure, configuration updates, token hash verification,
/// desktop application relaunching, and reverse-order compensation upon failure.
/// Establishes JOURNAL_READY_BOUNDARY = YES.
/// </summary>
public interface ISwitchTransactionExecutor
{
    /// <summary>
    /// Executes the validated plan atomically.
    /// </summary>
    Task<TargetSwitchResult> ExecuteAsync(CodexSwitchPlan plan, CancellationToken cancellationToken = default);
}
