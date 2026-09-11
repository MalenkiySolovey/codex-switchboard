using CodexSwitcher.App.Dialogs;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Abstrai as interações que precisam da UI (diálogos, janela de login), para o ViewModel
/// permanecer testável e sem dependência direta de XAML. Ver BUSINESS_RULES.md §4.2 (confirmação).
/// Extends feature-segregated dialog interfaces for backward compatibility and unified implementation.
/// </summary>
public interface IUiInteraction :
    IAccountDialogService,
    ITotpDialogService,
    IProviderDialogService,
    ISettingsDialogService,
    ICommonDialogService
{
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
