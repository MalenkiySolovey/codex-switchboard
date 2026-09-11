using System.Security.Cryptography;

namespace CodexSwitcher.Core.Abstractions;

public sealed record CodexRoutingState(
    string? ModelProvider,
    string? Model,
    IReadOnlyDictionary<string, CodexProviderBlock> SwitchboardProviders,
    string Fingerprint);

public sealed record CodexProviderBlock(
    string ProviderId,
    string Name,
    string BaseUrl,
    string WireApi,
    string BrokerCommand,
    IReadOnlyList<string> BrokerArgs,
    int TimeoutMs = 5000);

public class ConcurrentModificationException : InvalidOperationException
{
    public ConcurrentModificationException(string message) : base(message) { }
}

/// <summary>
/// Semantic owner of Codex routing configuration in config.toml.
/// Provides narrow, lossless mutations for root model_provider, model, and switchboard_* tables.
/// Protects against concurrent modifications and preserves all unrelated configuration tables/comments.
/// </summary>
public interface ICodexRoutingConfigStore
{
    string ComputeFingerprint(string configTomlPath);

    CodexRoutingState ReadRoutingState(string configTomlPath);

    string ApplySwitchboardRouting(
        string configTomlPath,
        CodexProviderBlock providerBlock,
        string model,
        string? expectedFingerprint = null);

    string UpdateProviderRoute(
        string configTomlPath,
        string providerId,
        string newBaseUrl,
        string? expectedFingerprint = null);

    string ReturnToOpenAi(
        string configTomlPath,
        string? model = null,
        string? expectedFingerprint = null);

    void RestoreExactBytes(string configTomlPath, byte[] exactBytes);
}
