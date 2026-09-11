using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.App.Dialogs;

/// <summary>
/// Dialog operations specific to API Provider creation, editing, key rotation, thread handoff, and route switching.
/// </summary>
public interface IProviderDialogService : ICommonDialogService
{
    Task<AddApiProviderResult?> PromptAddApiProviderAsync(IReadOnlyList<ProviderDescriptor> descriptors);
    Task<EditApiProviderResult?> PromptEditApiProviderAsync(ApiProviderProfile profile, ProviderDescriptor? descriptor);
    Task<string?> PromptRotateApiKeyAsync(string providerDisplayName);
    Task<CodexThreadSummary?> PromptContinueOnThreadAsync(IReadOnlyList<CodexThreadSummary> threads, string targetProviderName, string targetModel);
    Task<bool> ConfirmSwitchToApiAsync(string providerName, string model, string route);
}
