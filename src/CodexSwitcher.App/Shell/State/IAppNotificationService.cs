using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Shell.State;

/// <summary>
/// Defines the presentation contract for global application notifications.
/// Features consume this service to request info bars without direct coupling to ShellViewModel.
/// </summary>
public interface IAppNotificationService : INotifyPropertyChanged
{
    bool IsOpen { get; set; }
    string Title { get; }
    string Message { get; }
    InfoBarSeverity Severity { get; }

    void ShowInformation(string title, string message);
    void ShowSuccess(string title, string message);
    void ShowWarning(string title, string message);
    void ShowError(string title, string message);
    void Show(string title, string message, InfoBarSeverity severity);
    void Clear();
}
