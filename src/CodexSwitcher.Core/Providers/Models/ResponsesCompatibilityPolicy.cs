namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Controls compatibility behavior for the custom /responses wire API.
/// StandardResponses must not assume OpenAI-internal namespace/apps/plugin
/// tooling is accepted by a third-party Responses endpoint.
/// </summary>
public enum ResponsesCompatibilityPolicy
{
    /// <summary>Automatically determines compatibility based on route and model capabilities.</summary>
    Auto = 0,

    /// <summary>Standard OpenAI-compatible Responses API without proprietary internal extensions.</summary>
    StandardResponses = 1,

    /// <summary>Full OpenAI native Responses endpoint expecting internal namespace/apps/plugin structures.</summary>
    OpenAiNative = 2
}
