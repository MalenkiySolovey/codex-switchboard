namespace CodexSwitcher.App.Dialogs;

/// <summary>
/// Dialog operations required by Settings presentation.
/// </summary>
public interface ISettingsDialogService : ICommonDialogService
{
    Task OpenWindowsSignInOptionsAsync();
}
