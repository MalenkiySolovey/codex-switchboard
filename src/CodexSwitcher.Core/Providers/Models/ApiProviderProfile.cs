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
namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// User-configured instance of an API provider profile.
/// Isolates user configuration from generic catalog provider descriptors.
/// NEVER stores plaintext API keys here or in settings/logs.
/// </summary>
public sealed class ApiProviderProfile
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Immutable unique identifier for this profile instance.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Optional reference to a known catalog provider descriptor ID (e.g. "router-cheap").</summary>
    public string? CatalogProviderId { get; set; }

    /// <summary>
    /// Unique stable Codex provider ID (e.g. "switchboard_8f31c20a").
    /// Invariant: Every saved API key receives its own isolated provider ID in Codex config.
    /// NEVER share a single provider ID across different API keys.
    /// </summary>
    public string StableCodexProviderId { get; init; } = string.Empty;

    /// <summary>User-editable display nickname for this key/account.</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>Active base URL for this profile (supports user overrides over catalog defaults).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Selected route ID from catalog (e.g. "primary", "reserve") or "custom".</summary>
    public string? SelectedRouteId { get; set; }

    /// <summary>Selected model ID (e.g. "gpt-5.6-sol").</summary>
    public string? SelectedModel { get; set; }

    /// <summary>Wire API protocol for Codex inference (strictly "responses").</summary>
    public string WireApi { get; set; } = "responses";

    /// <summary>Safe key preview for UI (e.g. "sk-...1234"). NEVER store plaintext secret.</summary>
    public string KeyPreview { get; set; } = string.Empty;

    /// <summary>Status of the profile credentials.</summary>
    public ApiProviderProfileStatus Status { get; set; } = ApiProviderProfileStatus.Active;

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last time this profile was activated (UTC).</summary>
    public DateTimeOffset? LastSwitchedAt { get; set; }

    /// <summary>Display ordering index for UI.</summary>
    public int SortOrder { get; set; }

    /// <summary>Optional model configuration overrides for Codex runtime (context window, compaction, reasoning, etc.).</summary>
    public CodexModelOverrides? ModelOverrides { get; set; }

    /// <summary>Optional network and transport configuration overrides for the model provider block.</summary>
    public ApiProviderTransportOverrides? TransportOverrides { get; set; }

    /// <summary>Optional endpoint group ID linking multiple model configurations to the same base URL and credentials.</summary>
    public Guid? EndpointId { get; set; }

    /// <summary>Optional provider family/preset identifier (e.g. "deepseek", "xai", "openrouter", "modelflare", "hejuapi", "router-cheap").</summary>
    public string? ProviderPresetId { get; set; }

    /// <summary>Optional route or upstream pool label (e.g. "grok-award 0.01x", "grok-stable 0.11x").</summary>
    public string? RoutePoolLabel { get; set; }

    /// <summary>Cached list of model IDs discovered from the provider's /models endpoint.</summary>
    public List<string>? DiscoveredModels { get; set; }

    /// <summary>Full model inventory (discovered and manual entries) with enablement flags.</summary>
    public ApiProviderModelInventory? ModelInventory { get; set; }

    /// <summary>Latest overall qualification level for OpenAI Codex.</summary>
    public CodexCompatibilityLevel CompatibilityLevel { get; set; } = CodexCompatibilityLevel.Unknown;

    /// <summary>Latest capability probe report for this profile/model.</summary>
    public ProviderProbeReport? LastProbeReport { get; set; }

    /// <summary>User-facing display name.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Nickname) ? Nickname :
        !string.IsNullOrWhiteSpace(SelectedModel) ? $"{SelectedModel} ({Id.ToString()[..8]})" :
        $"API Provider {Id.ToString()[..8]}";

    public static string GenerateStableCodexProviderId(Guid id) =>
        $"switchboard_{id:N}"[..24];

    public static string ComputeKeyPreview(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return string.Empty;
        var trimmed = rawKey.Trim();
        if (trimmed.Length <= 8)
        {
            return $"{trimmed[..Math.Min(2, trimmed.Length)]}...";
        }
        var prefix = trimmed.Length >= 7 && trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..7]
            : trimmed[..3];
        var suffix = trimmed[^4..];
        return $"{prefix}...{suffix}";
    }
}

public enum ApiProviderProfileStatus
{
    Active = 0,
    CredentialMissing = 1,
    Archived = 2,
}
