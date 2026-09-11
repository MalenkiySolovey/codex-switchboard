using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.Shell.State;

/// <summary>
/// Lease-based, race-free implementation of <see cref="IAppBusyService"/>.
/// Preserves busy state until all concurrent operations/leases release.
/// </summary>
public sealed partial class AppBusyService : ObservableObject, IAppBusyService
{
    private readonly object _lock = new();
    private readonly List<BusyLease> _leases = [];

    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string? BusyText { get; private set; }

    public IDisposable Begin(string text)
    {
        var lease = new BusyLease(this, text);
        lock (_lock)
        {
            _leases.Add(lease);
            BusyText = text;
            IsBusy = true;
        }
        return lease;
    }

    public async Task RunAsync(string text, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using (Begin(text))
        {
            await action();
        }
    }

    public async Task<T> RunAsync<T>(string text, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using (Begin(text))
        {
            return await action();
        }
    }

    private void Release(BusyLease lease)
    {
        lock (_lock)
        {
            _leases.Remove(lease);
            if (_leases.Count > 0)
            {
                BusyText = _leases[^1].Text;
                IsBusy = true;
            }
            else
            {
                BusyText = null;
                IsBusy = false;
            }
        }
    }

    private sealed class BusyLease : IDisposable
    {
        private readonly AppBusyService _owner;
        private int _disposed;

        public string Text { get; }

        public BusyLease(AppBusyService owner, string text)
        {
            _owner = owner;
            Text = text;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(this);
            }
        }
    }
}
