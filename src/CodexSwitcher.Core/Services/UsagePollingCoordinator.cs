using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Services;

public sealed class AccountUsageUpdatedEventArgs : EventArgs
{
    public Guid ProfileId { get; }
    public AccountUsageState State { get; }

    public AccountUsageUpdatedEventArgs(Guid profileId, AccountUsageState state)
    {
        ProfileId = profileId;
        State = state;
    }
}

/// <summary>
/// Coordinates foreground automatic polling and manual refreshes across all managed profiles.
/// Suspends polling when the window is minimized or inactive.
/// Enforces non-overlapping, coalescing refresh execution with guaranteed lock release.
/// </summary>
public sealed class UsagePollingCoordinator : IDisposable
{
    private readonly IUsageService _usageService;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _batchGate = new();
    private readonly CancellationTokenSource _coordinatorCts = new();
    private Task<IReadOnlyDictionary<Guid, AccountUsageState>>? _activeBatchTask;
    private bool _disposed;

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan? BatchTimeoutOverride { get; set; }
    public bool IsForegroundActive { get; private set; } = true;
    public DateTimeOffset? LastAuthoritativeRefreshAt { get; private set; }
    public bool IsRefreshing { get; private set; }

    public event EventHandler<AccountUsageUpdatedEventArgs>? UsageUpdated;
    public event EventHandler<bool>? RefreshingStateChanged;

    public UsagePollingCoordinator(IUsageService usageService, IClock clock)
    {
        _usageService = usageService ?? throw new ArgumentNullException(nameof(usageService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Updates the application foreground state.
    /// When restored to foreground and elapsed time exceeds PollingInterval, triggers an immediate refresh.
    /// </summary>
    public async Task SetForegroundActiveAsync(bool active, IReadOnlyList<ProfileMetadata> profiles)
    {
        bool wasActive = IsForegroundActive;
        IsForegroundActive = active;

        if (!wasActive && active)
        {
            var now = _clock.UtcNow;
            if (LastAuthoritativeRefreshAt is null || (now - LastAuthoritativeRefreshAt.Value) >= PollingInterval)
            {
                await TriggerRefreshAllAsync(profiles).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Invoked when the periodic polling timer ticks.
    /// Skips execution if the app is minimized/inactive or if a refresh is already in progress.
    /// </summary>
    public async Task OnTimerTickAsync(IReadOnlyList<ProfileMetadata> profiles)
    {
        if (!IsForegroundActive || IsRefreshing)
            return;

        await TriggerRefreshAllAsync(profiles).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a coordinated batch refresh for all profiles with reentrancy protection and request coalescing.
    /// If a batch refresh is already active, coalesces and awaits the existing execution rather than silently failing.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, AccountUsageState>> TriggerRefreshAllAsync(
        IReadOnlyList<ProfileMetadata> profiles,
        CancellationToken cancellationToken = default)
    {
        if (profiles.Count == 0 || _disposed || _coordinatorCts.IsCancellationRequested)
            return new Dictionary<Guid, AccountUsageState>();

        Task<IReadOnlyDictionary<Guid, AccountUsageState>> batchTask;
        lock (_batchGate)
        {
            if (_activeBatchTask is { IsCompleted: false })
            {
                batchTask = _activeBatchTask;
            }
            else
            {
                batchTask = ExecuteBatchRefreshCoreAsync(profiles, cancellationToken);
                _activeBatchTask = batchTask;
            }
        }

        return await batchTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<Guid, AccountUsageState>> ExecuteBatchRefreshCoreAsync(
        IReadOnlyList<ProfileMetadata> profiles,
        CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(_coordinatorCts.Token).ConfigureAwait(false);
        try
        {
            SafeSetRefreshing(true);
            var now = _clock.UtcNow;

            // Notify UI that accounts are refreshing
            foreach (var profile in profiles)
            {
                var cached = _usageService.GetCached(profile.Id);
                var prevState = UsagePresentationMapper.MapFromCache(cached, profile.Id, now);
                SafeNotifyUsageUpdated(profile.Id, prevState.AsRefreshing());
            }

            IReadOnlyDictionary<Guid, UsageFetchResult> results;
            try
            {
                var timeout = BatchTimeoutOverride ?? TimeSpan.FromSeconds(30 + profiles.Count * 45);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _coordinatorCts.Token);
                linkedCts.CancelAfter(timeout);

                results = await _usageService.RefreshAllAsync(profiles, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                RestoreRefreshingCards(profiles);
                throw;
            }

            LastAuthoritativeRefreshAt = _clock.UtcNow;

            var mappedResults = new Dictionary<Guid, AccountUsageState>();
            foreach (var profile in profiles)
            {
                AccountUsageState mappedState;
                if (results.TryGetValue(profile.Id, out var fetchResult))
                {
                    mappedState = UsagePresentationMapper.MapFromFetchResult(fetchResult, profile.Id, LastAuthoritativeRefreshAt.Value);
                }
                else
                {
                    var cached = _usageService.GetCached(profile.Id);
                    mappedState = UsagePresentationMapper.MapFromCache(cached, profile.Id, LastAuthoritativeRefreshAt.Value);
                }

                mappedResults[profile.Id] = mappedState;
                SafeNotifyUsageUpdated(profile.Id, mappedState);
            }

            return mappedResults;
        }
        catch (Exception)
        {
            RestoreRefreshingCards(profiles);
            throw;
        }
        finally
        {
            try
            {
                SafeSetRefreshing(false);
            }
            finally
            {
                _refreshLock.Release();
            }
        }
    }

    private void RestoreRefreshingCards(IReadOnlyList<ProfileMetadata> profiles)
    {
        var now = _clock.UtcNow;
        foreach (var profile in profiles)
        {
            var cached = _usageService.GetCached(profile.Id);
            var restored = UsagePresentationMapper.MapFromCache(cached, profile.Id, now);
            SafeNotifyUsageUpdated(profile.Id, restored);
        }
    }

    /// <summary>
    /// Executes a manual refresh for an individual profile.
    /// Guarantees that the card will exit the Refreshing state upon completion, cancellation, or failure.
    /// </summary>
    public async Task<AccountUsageState> TriggerRefreshAccountAsync(
        ProfileMetadata profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var now = _clock.UtcNow;
        var cached = _usageService.GetCached(profile.Id);
        var refreshingState = UsagePresentationMapper.MapFromCache(cached, profile.Id, now).AsRefreshing();
        SafeNotifyUsageUpdated(profile.Id, refreshingState);

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _coordinatorCts.Token);
            var fetchResult = await _usageService.RefreshAsync(profile, force: true, linkedCts.Token).ConfigureAwait(false);
            var completedState = UsagePresentationMapper.MapFromFetchResult(fetchResult, profile.Id, _clock.UtcNow);
            SafeNotifyUsageUpdated(profile.Id, completedState);
            return completedState;
        }
        catch (Exception)
        {
            var currentCached = _usageService.GetCached(profile.Id);
            var restored = UsagePresentationMapper.MapFromCache(currentCached, profile.Id, _clock.UtcNow);
            SafeNotifyUsageUpdated(profile.Id, restored);
            throw;
        }
    }

    /// <summary>
    /// Invalidates usage cache and resets presentation state for the specified profile.
    /// </summary>
    public void Invalidate(Guid profileId)
    {
        _usageService.Invalidate(profileId);
        var cleared = AccountUsageState.NeverLoadedState(profileId);
        SafeNotifyUsageUpdated(profileId, cleared);
    }

    public void Stop()
    {
        try
        {
            _coordinatorCts.Cancel();
        }
        catch { }
        SafeSetRefreshing(false);
        UsageUpdated = null;
        RefreshingStateChanged = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _coordinatorCts.Dispose();
        _refreshLock.Dispose();
    }

    private void SafeNotifyUsageUpdated(Guid profileId, AccountUsageState state)
    {
        try
        {
            UsageUpdated?.Invoke(this, new AccountUsageUpdatedEventArgs(profileId, state));
        }
        catch { }
    }

    private void SafeSetRefreshing(bool value)
    {
        try
        {
            SetRefreshing(value);
        }
        catch { }
    }

    private void SetRefreshing(bool value)
    {
        if (IsRefreshing != value)
        {
            IsRefreshing = value;
            RefreshingStateChanged?.Invoke(this, value);
        }
    }
}