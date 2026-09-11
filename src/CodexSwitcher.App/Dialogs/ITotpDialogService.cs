using CodexSwitcher.App.Services;

namespace CodexSwitcher.App.Dialogs;

/// <summary>
/// Dialog operations specific to TOTP 2FA secret management, degraded Windows auth fallbacks, and settings navigation.
/// </summary>
public interface ITotpDialogService : ICommonDialogService
{
    Task<TotpSetupResult?> PromptTotpSetupAsync(string accountName, bool isCurrentlyConfigured);
    Task<TransientVerificationChoice> PromptTransientVerificationFallbackAsync(string message);
    Task OpenWindowsSignInOptionsAsync();
}

public enum TransientVerificationChoice
{
    TryAgain,
    ShowCodeOnce,
    Cancel
}

public enum TotpSetupAction
{
    SaveNewKey,
    RemoveKey,
    Cancel
}

public sealed record TotpSetupResult(TotpSetupAction Action, string? ProvisioningKey = null);
