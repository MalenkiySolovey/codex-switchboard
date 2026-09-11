namespace CodexSwitcher.App.Dialogs;

/// <summary>
/// Common dialog operations: alerts, confirmations, simple text prompts.
/// </summary>
public interface ICommonDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string okText, bool destructive = false);
    Task<string?> PromptTextAsync(string title, string prompt, string initialValue, string okText);
    Task ShowMessageAsync(string title, string message);
}
