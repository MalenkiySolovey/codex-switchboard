using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Models;

public enum TargetSwitchOutcome
{
    Success = 1,
    SuccessWithReopenWarning = 2,
    RolledBack = 3,
    Failed = 4,
    AbortedProcessRemnant = 5,
    NoOp = 6
}

public sealed record TargetSwitchResult(
    TargetSwitchOutcome Outcome,
    string Message,
    ActiveTarget Target,
    ErrorInfo? Error = null,
    IReadOnlyList<CodexProcessInfo>? ClosedProcesses = null,
    IReadOnlyList<CodexProcessInfo>? ReopenFailures = null);
