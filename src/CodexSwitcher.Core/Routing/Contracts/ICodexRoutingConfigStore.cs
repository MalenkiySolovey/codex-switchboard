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
using CodexSwitcher.Core.Routing.Contracts;
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
using System.Security.Cryptography;

namespace CodexSwitcher.Core.Routing.Contracts;

public sealed record CodexRoutingState(
    string? ModelProvider,
    string? Model,
    IReadOnlyDictionary<string, CodexProviderBlock> SwitchboardProviders,
    string Fingerprint,
    string? ModelCatalogJson = null);

public sealed record CodexProviderBlock(
    string ProviderId,
    string Name,
    string BaseUrl,
    string WireApi,
    string BrokerCommand,
    IReadOnlyList<string> BrokerArgs,
    int TimeoutMs = 5000,
    ulong? RequestMaxRetries = null,
    ulong? StreamMaxRetries = null,
    ulong? StreamIdleTimeoutMs = null,
    ulong? WebSocketConnectTimeoutMs = null,
    bool? SupportsWebSockets = null,
    bool? SupportsStandaloneWebSearch = null,
    IReadOnlyDictionary<string, string>? QueryParams = null,
    IReadOnlyDictionary<string, string>? HttpHeaders = null,
    IReadOnlyDictionary<string, string>? EnvHttpHeaders = null,
    ResponsesCompatibilityPolicy ResponsesPolicy = ResponsesCompatibilityPolicy.Auto);

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
        CodexModelOverrides? modelOverrides = null,
        string? modelCatalogJson = null,
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
