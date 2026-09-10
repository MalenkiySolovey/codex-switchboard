namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Abstraction for dispatching presentation updates to the UI thread.
/// Pure .NET interface with no dependencies on Microsoft.UI.Xaml.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Gets a value indicating whether the caller has access to the UI thread.
    /// </summary>
    bool HasThreadAccess { get; }

    /// <summary>
    /// Enqueues the specified action for execution on the UI thread.
    /// If the current thread already has thread access, executes immediately.
    /// </summary>
    void Enqueue(Action action);
}