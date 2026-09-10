using CodexSwitcher.Core.Abstractions;
using Microsoft.UI.Dispatching;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Marshals presentation events onto the WinUI <see cref=DispatcherQueue/> thread.
/// Guarantees that UI-bound collections, ViewModels, and property changes occur on the UI thread.
/// </summary>
public sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUiDispatcher(DispatcherQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _queue.TryEnqueue(() => action());
        }
    }
}