using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Determines the current active inference target configured in config.toml,
/// taking into account known API provider profiles and current ChatGPT auth.json state.
/// </summary>
public interface ICodexActiveTargetResolver
{
    ActiveTarget ResolveActiveTarget(
        string configTomlPath,
        Guid? activeChatGptProfileId = null,
        string? activeChatGptEmail = null);
}
