using System;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Scopes for Codex auto-compact token limit.
/// Matches Codex upstream configuration: "total" and "body_after_prefix".
/// </summary>
public enum CompactLimitScope
{
    Default = 0,
    Total = 1,
    BodyAfterPrefix = 2,
}

/// <summary>
/// Supported model reasoning effort levels in Codex CLI.
/// Matches upstream Codex ModelReasoningEffort variants.
/// </summary>
public enum CodexReasoningEffort
{
    Default = 0,
    None = 1,
    Minimal = 2,
    Low = 3,
    Medium = 4,
    High = 5,
    XHigh = 6,
}

/// <summary>
/// Reasoning summary configuration in Codex CLI.
/// Matches upstream Codex ReasoningSummary variants.
/// </summary>
public enum CodexReasoningSummary
{
    Default = 0,
    Auto = 1,
    Concise = 2,
    Detailed = 3,
    None = 4,
}

/// <summary>
/// Output verbosity level in Codex CLI.
/// Matches upstream Codex ModelVerbosity variants.
/// </summary>
public enum CodexVerbosity
{
    Default = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// Typed, validated model configuration overrides for an API provider profile.
/// Configures top-level Codex options such as context window, auto-compaction, reasoning effort, etc.
/// Null or Default values mean Switchboard will NOT write an override to config.toml.
/// </summary>
public sealed class CodexModelOverrides
{
    /// <summary>
    /// Custom maximum context window in tokens (e.g. 256000, 500000, 1000000).
    /// Must be greater than 0 if specified.
    /// </summary>
    public long? ContextWindowTokens { get; set; }

    /// <summary>
    /// Custom token limit threshold at which Codex triggers auto-compaction.
    /// Must be greater than 0 and less than or equal to ContextWindowTokens if specified.
    /// </summary>
    public long? AutoCompactTokenLimit { get; set; }

    /// <summary>
    /// Compaction token counting scope (default, total, body_after_prefix).
    /// </summary>
    public CompactLimitScope AutoCompactTokenLimitScope { get; set; } = CompactLimitScope.Default;

    /// <summary>
    /// Model reasoning effort override.
    /// </summary>
    public CodexReasoningEffort ReasoningEffort { get; set; } = CodexReasoningEffort.Default;

    /// <summary>
    /// Reasoning summary mode override.
    /// </summary>
    public CodexReasoningSummary ReasoningSummary { get; set; } = CodexReasoningSummary.Default;

    /// <summary>
    /// Output verbosity level override.
    /// </summary>
    public CodexVerbosity Verbosity { get; set; } = CodexVerbosity.Default;

    /// <summary>
    /// Tool output token limit. Must be greater than 0 if specified.
    /// </summary>
    public int? ToolOutputTokenLimit { get; set; }

    /// <summary>
    /// Validates self consistency of model override options.
    /// </summary>
    public void Validate()
    {
        if (ContextWindowTokens.HasValue && ContextWindowTokens.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ContextWindowTokens), "Context window tokens must be greater than zero.");
        }

        if (AutoCompactTokenLimit.HasValue)
        {
            if (AutoCompactTokenLimit.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(AutoCompactTokenLimit), "Auto-compact token limit must be greater than zero.");
            }

            if (ContextWindowTokens.HasValue && AutoCompactTokenLimit.Value > ContextWindowTokens.Value)
            {
                throw new ArgumentException("Auto-compact token limit cannot exceed the configured context window tokens.", nameof(AutoCompactTokenLimit));
            }
        }

        if (ToolOutputTokenLimit.HasValue && ToolOutputTokenLimit.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ToolOutputTokenLimit), "Tool output token limit must be greater than zero.");
        }
    }
}
