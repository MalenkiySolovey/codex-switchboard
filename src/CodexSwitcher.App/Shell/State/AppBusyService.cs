using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
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
