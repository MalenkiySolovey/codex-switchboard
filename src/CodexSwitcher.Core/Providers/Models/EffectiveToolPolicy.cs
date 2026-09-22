using System;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Domain model representing the effective Codex tool surface permitted on an active inference route.
/// StandardResponses routes only permit extended tools that are qualified by evidence.
/// OpenAiNative routes preserve the complete native tool surface.
/// Crucial invariant: Standard function tools must remain enabled unless explicitly unsupported.
/// </summary>
public sealed record EffectiveToolPolicy(
    bool AllowStandardFunctionTools,
    bool AllowCustomFreeformApplyPatch,
    bool AllowToolSearch,
    bool AllowHostedWebSearch,
    bool AllowStandaloneWebSearch,
    bool AllowNamespaceTools,
    bool AllowMultiAgent)
{
    public static EffectiveToolPolicy ForOpenAiNative() =>
        new(
            AllowStandardFunctionTools: true,
            AllowCustomFreeformApplyPatch: true,
            AllowToolSearch: true,
            AllowHostedWebSearch: true,
            AllowStandaloneWebSearch: true,
            AllowNamespaceTools: true,
            AllowMultiAgent: true);

    public static EffectiveToolPolicy Resolve(
        ResponsesCompatibilityPolicy policy,
        RouteCapabilities? routeCapabilities,
        ModelCapabilities? modelCapabilities = null)
    {
        if (policy == ResponsesCompatibilityPolicy.OpenAiNative)
        {
            return ForOpenAiNative();
        }

        // For StandardResponses or Auto: derive strictly from route capabilities & evidence
        var isExplicitStandardResponses = policy == ResponsesCompatibilityPolicy.StandardResponses;

        var allowFunctions = routeCapabilities == null ||
            routeCapabilities.StandardFunctionTools.State is not CapabilityEvidenceState.ProbeFailed;

        var allowCustomApplyPatch = isExplicitStandardResponses
            ? routeCapabilities?.ApplyPatchFreeform.State == CapabilityEvidenceState.ProbePassed
            : routeCapabilities?.ApplyPatchFreeform.State != CapabilityEvidenceState.ProbeFailed;

        var allowToolSearch = isExplicitStandardResponses
            ? routeCapabilities?.ToolSearch.State == CapabilityEvidenceState.ProbePassed
            : routeCapabilities?.ToolSearch.State != CapabilityEvidenceState.ProbeFailed;

        var allowHostedSearch = isExplicitStandardResponses
            ? routeCapabilities?.HostedWebSearch.State == CapabilityEvidenceState.ProbePassed
            : routeCapabilities?.HostedWebSearch.State != CapabilityEvidenceState.ProbeFailed;

        var allowStandaloneSearch = routeCapabilities != null &&
            routeCapabilities.StandaloneWebSearch.State == CapabilityEvidenceState.ProbePassed;

        var allowNamespaceTools = isExplicitStandardResponses
            ? routeCapabilities?.NamespaceTools.State == CapabilityEvidenceState.ProbePassed
            : routeCapabilities?.NamespaceTools.State != CapabilityEvidenceState.ProbeFailed;

        var allowMultiAgent = isExplicitStandardResponses
            ? routeCapabilities?.AppsPlugins.State == CapabilityEvidenceState.ProbePassed
            : routeCapabilities?.AppsPlugins.State != CapabilityEvidenceState.ProbeFailed;

        return new EffectiveToolPolicy(
            AllowStandardFunctionTools: allowFunctions,
            AllowCustomFreeformApplyPatch: allowCustomApplyPatch,
            AllowToolSearch: allowToolSearch,
            AllowHostedWebSearch: allowHostedSearch,
            AllowStandaloneWebSearch: allowStandaloneSearch,
            AllowNamespaceTools: allowNamespaceTools,
            AllowMultiAgent: allowMultiAgent);
    }
}
