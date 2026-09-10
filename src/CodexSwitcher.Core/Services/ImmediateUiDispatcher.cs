using CodexSwitcher.Core.Abstractions;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Immediate pass-through implementation of <see cref=IUiDispatcher/> for unit testing and offline environments.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public static ImmediateUiDispatcher Instance { get; } = new();

    public bool HasThreadAccess => true;

    public void Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}