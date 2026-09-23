namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Verifies a newly projected custom model catalog through an owned instance
/// of the currently resolved production Codex app-server. This is deliberately
/// separate from static JSON validation because app-server model/list is the
/// runtime postcondition consumed by Codex clients.
/// </summary>
public interface ICodexRuntimeModelCatalogVerifier
{
    Task<CodexRuntimeModelCatalogVerification> VerifyAsync(
        string expectedProvider,
        string expectedSelectedModel,
        IReadOnlyCollection<string> expectedEnabledModels,
        CancellationToken cancellationToken = default);
}

public sealed record CodexRuntimeModelCatalogVerification(
    bool Succeeded,
    IReadOnlyCollection<string> ObservedModels,
    string? FailureReason = null);
