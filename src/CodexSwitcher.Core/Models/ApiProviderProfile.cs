namespace CodexSwitcher.Core.Models;

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

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last time this profile was activated (UTC).</summary>
    public DateTimeOffset? LastSwitchedAt { get; set; }

    /// <summary>Display ordering index for UI.</summary>
    public int SortOrder { get; set; }

    /// <summary>User-facing display name.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Nickname) ? Nickname :
        !string.IsNullOrWhiteSpace(SelectedModel) ? $"{SelectedModel} ({Id.ToString()[..8]})" :
        $"API Provider {Id.ToString()[..8]}";

    public static string GenerateStableCodexProviderId(Guid id) =>
        $"switchboard_{id:N}"[..24];
}
