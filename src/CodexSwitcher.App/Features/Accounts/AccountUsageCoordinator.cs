using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
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
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
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
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Features.Accounts;

/// <summary>
/// Coordinates account quota usage presentation, cache restoration,
/// periodic polling, countdown updates, and foreground lifecycle.
/// </summary>
public sealed class AccountUsageCoordinator : IDisposable
{
    private static Strings Loc => Strings.Current;

    private readonly IUsageService _usageService;
    private readonly UsagePollingCoordinator _pollingCoordinator;
    private readonly IClock _clock;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLifetime _appLifetime;
    private readonly IAppNotificationService _notifications;

    private readonly Dictionary<Guid, AccountUsageViewModel> _usages = [];
    private DispatcherTimer? _countdownTimer;
    private DispatcherTimer? _pollingTimer;
    private CancellationTokenSource? _startupRefreshCts;

    public bool IsRefreshingUsage { get; private set; }

    public event Action<bool>? RefreshingStateChanged;

    public AccountUsageCoordinator(
        IUsageService usageService,
        UsagePollingCoordinator pollingCoordinator,
        IClock clock,
        IUiDispatcher dispatcher,
        IAppLifetime appLifetime,
        IAppNotificationService notifications)
    {
        _usageService = usageService ?? throw new ArgumentNullException(nameof(usageService));
        _pollingCoordinator = pollingCoordinator ?? throw new ArgumentNullException(nameof(pollingCoordinator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _appLifetime = appLifetime ?? throw new ArgumentNullException(nameof(appLifetime));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));

        _pollingCoordinator.UsageUpdated += OnUsageUpdated;
        _pollingCoordinator.RefreshingStateChanged += OnRefreshingStateChanged;
    }

    public AccountUsageViewModel GetOrCreateUsageVm(Guid profileId)
    {
        if (!_usages.TryGetValue(profileId, out var vm))
        {
            vm = new AccountUsageViewModel(profileId);
            _usages[profileId] = vm;
        }
        return vm;
    }

    public async Task LoadCacheAsync(bool isPortuguese)
    {
        await _usageService.LoadCacheAsync();
        var cachedMap = _usageService.GetAllCached();
        var now = _clock.UtcNow;
        foreach (var (profileId, cacheEntry) in cachedMap)
        {
            var state = UsagePresentationMapper.MapFromCache(cacheEntry, profileId, now, isPortuguese);
            var vm = GetOrCreateUsageVm(profileId);
            vm.ApplyState(state, now);
        }
    }

    public void StartTimers(Func<IReadOnlyList<ProfileMetadata>> getProfiles)
    {
        if (_countdownTimer is null)
        {
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _countdownTimer.Tick += (_, _) =>
            {
                var now = _clock.UtcNow;
                foreach (var u in _usages.Values)
                    u.UpdateCountdowns(now);
            };
            _countdownTimer.Start();
        }

        if (_pollingTimer is null)
        {
            _pollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _pollingTimer.Tick += async (_, _) =>
            {
                await _pollingCoordinator.OnTimerTickAsync(getProfiles());
            };
            _pollingTimer.Start();
        }
    }

    public void StopTimers()
    {
        _countdownTimer?.Stop();
        _pollingTimer?.Stop();
    }

    public void SetForegroundActive(bool active, IReadOnlyList<ProfileMetadata> profiles)
    {
        _ = _pollingCoordinator.SetForegroundActiveAsync(active, profiles);
    }

    public Task TriggerBackgroundRefreshForProfiles(IReadOnlyList<ProfileMetadata> profiles)
    {
        if (profiles.Count == 0)
            return Task.CompletedTask;

        _startupRefreshCts?.Cancel();
        _startupRefreshCts?.Dispose();
        _startupRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.ApplicationStopping);

        var token = _startupRefreshCts.Token;
        return Task.Run(async () =>
        {
            try
            {
                await _pollingCoordinator.TriggerRefreshAllAsync(profiles, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _dispatcher.Enqueue(() =>
                {
                    ShowInfo(Loc.ErrorTitle, ex.Message, InfoBarSeverity.Warning);
                });
            }
        }, token);
    }

    public async Task RefreshAllAsync(IReadOnlyList<ProfileMetadata> profiles)
    {
        if (profiles.Count == 0) return;
        try
        {
            await _pollingCoordinator.TriggerRefreshAllAsync(profiles);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowInfo(Loc.ErrorTitle, ex.Message, InfoBarSeverity.Warning);
        }
    }

    public async Task RefreshAccountAsync(ProfileMetadata profile)
    {
        try
        {
            await _pollingCoordinator.TriggerRefreshAccountAsync(profile);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowInfo(Loc.ErrorTitle, ex.Message, InfoBarSeverity.Warning);
        }
    }

    public void Invalidate(Guid profileId)
    {
        _pollingCoordinator.Invalidate(profileId);
        _usages.Remove(profileId);
    }

    private void OnUsageUpdated(object? sender, AccountUsageUpdatedEventArgs e)
    {
        _dispatcher.Enqueue(() =>
        {
            var vm = GetOrCreateUsageVm(e.ProfileId);
            vm.ApplyState(e.State, _clock.UtcNow);
        });
    }

    private void OnRefreshingStateChanged(object? sender, bool isRefreshing)
    {
        _dispatcher.Enqueue(() =>
        {
            IsRefreshingUsage = isRefreshing;
            RefreshingStateChanged?.Invoke(isRefreshing);
        });
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        _notifications.Show(title, message, severity);
    }

    public void Dispose()
    {
        StopTimers();
        _startupRefreshCts?.Cancel();
        _startupRefreshCts?.Dispose();
        _startupRefreshCts = null;

        _pollingCoordinator.UsageUpdated -= OnUsageUpdated;
        _pollingCoordinator.RefreshingStateChanged -= OnRefreshingStateChanged;
    }
}
