using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.App.Dialogs;

/// <summary>
/// Dialog operations specific to account lifecycle, switching confirmation, OAuth, and file import/export.
/// </summary>
public interface IAccountDialogService : ICommonDialogService
{
    Task<bool> ConfirmSwitchAsync(SwitchPlan plan);
    Task<byte[]?> RunEphemeralLoginAsync();
    Task<string?> PickImportFileAsync();
    Task<bool> SaveExportFileAsync(string suggestedFileName, string contents);
    Task<SubscriptionTracking?> PromptSubscriptionTrackingAsync(string accountName, SubscriptionTracking? current, DetectedSubscriptionInfo? detected = null);
}
