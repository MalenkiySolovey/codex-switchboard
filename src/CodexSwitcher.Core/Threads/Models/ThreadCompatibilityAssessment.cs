using System;
using System.Collections.Generic;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Threads.Models;

/// <summary>
/// Result of evaluating whether an existing Codex thread's history contains items
/// (such as OpenAI proprietary web_search_call, custom apply_patch, or tool_search)
/// that would be rejected by a target third-party StandardResponses provider.
/// </summary>
public sealed record ThreadCompatibilityAssessment(
    string ThreadId,
    bool RequiresFreshThread,
    IReadOnlyList<string> IncompatibleFeatures,
    string? RecommendedAction)
{
    public static ThreadCompatibilityAssessment Compatible(string threadId) =>
        new(threadId, false, Array.Empty<string>(), "Thread is compatible with target tool policy and can continue directly.");

    public static ThreadCompatibilityAssessment Incompatible(
        string threadId,
        IReadOnlyList<string> incompatibleFeatures,
        string recommendedAction = "Start a fresh thread or fork to avoid third-party provider rejection.") =>
        new(threadId, true, incompatibleFeatures, recommendedAction);

    public static ThreadCompatibilityAssessment EvaluateHistoryItems(
        string threadId,
        IEnumerable<string> historyItemTypes,
        EffectiveToolPolicy targetPolicy)
    {
        ArgumentNullException.ThrowIfNull(targetPolicy);

        var incompatible = new List<string>();
        foreach (var item in historyItemTypes)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;

            if (!targetPolicy.AllowHostedWebSearch &&
                (item.Contains("web_search", StringComparison.OrdinalIgnoreCase) ||
                 item.Contains("search_result", StringComparison.OrdinalIgnoreCase)))
            {
                if (!incompatible.Contains("Hosted web_search history item"))
                    incompatible.Add("Hosted web_search history item");
            }

            if (!targetPolicy.AllowCustomFreeformApplyPatch &&
                item.Contains("apply_patch", StringComparison.OrdinalIgnoreCase))
            {
                if (!incompatible.Contains("Custom apply_patch tool history item"))
                    incompatible.Add("Custom apply_patch tool history item");
            }

            if (!targetPolicy.AllowToolSearch &&
                item.Contains("tool_search", StringComparison.OrdinalIgnoreCase))
            {
                if (!incompatible.Contains("Tool search history item"))
                    incompatible.Add("Tool search history item");
            }

            if (!targetPolicy.AllowNamespaceTools &&
                (item.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
                 item.Contains("mcp::", StringComparison.OrdinalIgnoreCase)))
            {
                if (!incompatible.Contains("Namespace tool history item"))
                    incompatible.Add("Namespace tool history item");
            }
        }

        if (incompatible.Count > 0)
        {
            return Incompatible(threadId, incompatible);
        }

        return Compatible(threadId);
    }
}
