using System;
using System.Threading.Tasks;
using CodexSwitcher.App.Dialogs.Common;
using CodexSwitcher.App.Dialogs.Shared;

namespace CodexSwitcher.App.Dialogs.Settings;

/// <summary>
/// Implementation of dialog operations required by Settings presentation.
/// </summary>
public sealed class SettingsDialogService : CommonDialogService, ISettingsDialogService
{
    public SettingsDialogService(IDialogHost host) : base(host)
    {
    }

    public Task OpenWindowsSignInOptionsAsync() => DialogPresentationHelpers.OpenWindowsSignInOptionsAsync();
}
