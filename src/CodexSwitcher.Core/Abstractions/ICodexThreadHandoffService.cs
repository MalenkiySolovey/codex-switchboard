namespace CodexSwitcher.Core.Abstractions;

public sealed record CodexThreadSummary(
    string Id,
    string? Name,
    string? ModelProvider,
    string? Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Cwd);

public sealed record ThreadForkResult(
    string ForkedThreadId,
    string SourceThreadId,
    string TargetModelProvider,
    string TargetModel,
    string? Name);

/// <summary>
/// Service managing cross-provider thread continuation via the official Codex app-server protocol.
/// Explicitly passes modelProvider and model on thread/fork while keeping source thread immutable.
/// Strictly avoids direct SQLite or rollout file mutations.
/// </summary>
public interface ICodexThreadHandoffService
{
    Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<ThreadForkResult> ForkThreadAsync(
        string sourceThreadId,
        string targetModelProvider,
        string targetModel,
        string? newName = null,
        CancellationToken cancellationToken = default);
}
