using System.Collections.Generic;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Providers.Contracts;

/// <summary>
/// Resolved effective configuration for an individual model entry in a Codex model catalog.
/// Intersects model facts, provider route capabilities, runtime capabilities, and user overrides.
/// </summary>
public sealed record EffectiveModelDescriptor(
    string Slug,
    string DisplayName,
    long ContextWindow,
    long MaxContextWindow,
    CodexReasoningEffort ReasoningEffort,
    CodexVerbosity Verbosity,
    bool AllowFreeformApplyPatch,
    bool AllowToolSearch,
    bool AllowHostedWebSearch,
    bool SupportsImages,
    bool SupportsStreaming,
    int Priority = 1);

/// <summary>
/// Domain service that determines the effective model descriptor and builds compliant catalog dictionary entries.
/// </summary>
public interface IEffectiveModelDescriptorResolver
{
    EffectiveModelDescriptor ResolveDescriptor(
        ApiProviderModelItem modelItem,
        ApiProviderProfile profile,
        ProviderDescriptor? catalogDescriptor = null,
        EffectiveToolPolicy? toolPolicy = null,
        bool isLegacyRuntime = false,
        bool isSelectedModel = false);

    Dictionary<string, object?> BuildCatalogEntry(
        EffectiveModelDescriptor descriptor,
        bool isLegacyRuntime = false);
}
