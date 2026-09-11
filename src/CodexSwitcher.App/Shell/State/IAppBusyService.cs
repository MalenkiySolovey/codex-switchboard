using System.ComponentModel;

namespace CodexSwitcher.App.Shell.State;

/// <summary>
/// Presentation contract for global application busy overlay state.
/// Supports overlapping/nested operations safely via a reference-tracked lease model.
/// </summary>
public interface IAppBusyService : INotifyPropertyChanged
{
    bool IsBusy { get; }
    string? BusyText { get; }

    IDisposable Begin(string text);
    Task RunAsync(string text, Func<Task> action);
    Task<T> RunAsync<T>(string text, Func<Task<T>> action);
}
