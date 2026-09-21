namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Overall qualification status of an API endpoint or model for OpenAI Codex.
/// Strictly distinguishes direct Responses API compatibility from incompatible protocols
/// (Chat Completions, Anthropic Messages, Gemini native, etc.).
/// </summary>
public enum CodexCompatibilityLevel
{
    /// <summary>Compatibility has not been verified yet.</summary>
    Unknown = 0,

    /// <summary>Fully Codex compatible via direct Responses API; both protocol probes and isolated Codex execution passed.</summary>
    CodexCompatible = 1,

    /// <summary>Partially compatible; basic inference/tools passed, but proprietary OpenAI namespace/advanced Codex tooling is rejected.</summary>
    PartiallyCompatible = 2,

    /// <summary>Not compatible with Codex; endpoint does not implement or rejects the wire_api = "responses" protocol.</summary>
    NotCompatible = 3,
}
