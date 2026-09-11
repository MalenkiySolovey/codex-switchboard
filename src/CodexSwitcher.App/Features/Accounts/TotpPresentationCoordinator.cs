using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.App.ViewModels;
using CodexSwitcher.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Features.Accounts;

/// <summary>
/// Coordinates TOTP 2FA presentation state, Windows verification authorization gates,
/// code computation, clipboard copy, and single-timer presentation lifecycle.
/// Strictly enforces that persistent secret seeds are never stored or exposed in the presentation layer.
/// </summary>
public sealed class TotpPresentationCoordinator : IDisposable
{
    private static Strings Loc => Strings.Current;

    private readonly ITotpCredentialStore? _totpStore;
    private readonly ITotpRevealAuthorizationService? _authService;
    private readonly IClock _clock;
    private readonly ITotpDialogService _ui;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLifetime _appLifetime;
    private readonly IAppNotificationService _notifications;

    private DispatcherTimer? _totpPresentationTimer;
    private bool _hasShownDegradedNoticeThisSession;

    public TotpPresentationCoordinator(
        ITotpCredentialStore? totpStore,
        ITotpRevealAuthorizationService? authService,
        IClock clock,
        ITotpDialogService ui,
        IUiDispatcher dispatcher,
        IAppLifetime appLifetime,
        IAppNotificationService notifications)
    {
        _totpStore = totpStore;
        _authService = authService;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _appLifetime = appLifetime ?? throw new ArgumentNullException(nameof(appLifetime));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    }

    public bool HasCredential(Guid profileId) => _totpStore?.HasCredential(profileId) ?? false;

    public async Task RevealTotpAsync(AccountItemViewModel? item, IEnumerable<AccountItemViewModel> allAccounts)
    {
        if (item is null || !item.HasTotpConfigured || _totpStore is null) return;

        if (item.IsTotpRevealed)
        {
            item.ResetTotpPresentation();
            EnsureTotpPresentationTimer(allAccounts);
            return;
        }

        // Single visible code policy: hide any other currently revealed profile
        foreach (var other in allAccounts)
        {
            if (other != item && other.IsTotpRevealed)
                other.ResetTotpPresentation();
        }

        bool isDegradedReveal = false;
        // Hard security ordering: verification must succeed or fallback must be authorized before secret decryption
        if (_authService is not null && _authService.IsVerificationRequired())
        {
            var outcome = await _authService.EnsureAuthorizedAsync(Loc.WindowsVerificationPromptMessage);

            if (outcome.IsDegradedFallback)
            {
                isDegradedReveal = true;
                if (!_hasShownDegradedNoticeThisSession)
                {
                    _hasShownDegradedNoticeThisSession = true;
                    ShowInfo(Loc.WarningTitle, Loc.WindowsVerificationDegradedNotice, InfoBarSeverity.Informational);
                }
            }
            else if (outcome.IsTemporarilyUnavailable)
            {
                var choice = await _ui.PromptTransientVerificationFallbackAsync(Loc.WindowsVerificationTransientMessage);
                if (choice == TransientVerificationChoice.TryAgain)
                {
                    await RevealTotpAsync(item, allAccounts);
                    return;
                }
                else if (choice == TransientVerificationChoice.ShowCodeOnce)
                {
                    isDegradedReveal = true;
                }
                else
                {
                    return;
                }
            }
            else if (!outcome.Success)
            {
                if (outcome.Action == TotpAuthorizationAction.Canceled || outcome.Status == WindowsVerificationResult.Canceled || outcome.PasswordStatus == WindowsPasswordVerificationResult.Canceled)
                {
                    ShowInfo(Loc.WarningTitle, Loc.WindowsVerificationCanceled, InfoBarSeverity.Informational);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.InvalidCredentials)
                {
                    ShowInfo(Loc.WarningTitle, Loc.WindowsPasswordIncorrect, InfoBarSeverity.Warning);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.DifferentUser)
                {
                    ShowInfo(Loc.WarningTitle, Loc.WindowsDifferentUser, InfoBarSeverity.Warning);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.AccountLocked)
                {
                    ShowInfo(Loc.WarningTitle, Loc.WindowsAccountLocked, InfoBarSeverity.Warning);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.PasswordExpired)
                {
                    ShowInfo(Loc.WarningTitle, Loc.WindowsPasswordExpired, InfoBarSeverity.Warning);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.AccountRestricted)
                {
                    ShowInfo(Loc.WarningTitle, Loc.WindowsAccountRestricted, InfoBarSeverity.Warning);
                }
                else
                {
                    ShowInfo(Loc.WarningTitle, Loc.WindowsVerificationFailed, InfoBarSeverity.Warning);
                }
                return;
            }
        }

        // Post-verification safety re-checks
        if (_appLifetime.IsStopping) return;
        if (!_totpStore.HasCredential(item.Id))
        {
            item.HasTotpConfigured = false;
            return;
        }

        var now = _clock.UtcNow;
        if (_totpStore.TryComputeCode(item.Id, now, out var code, out var error))
        {
            item.IsTotpRevealed = true;
            item.IsDegradedReveal = isDegradedReveal;
            item.TotpCodeText = code.Formatted;
            item.TotpSecondsRemaining = code.SecondsRemaining;
            item.TotpCountdownText = $"{code.SecondsRemaining}s";

            // Two-timer invariant: min(10s, authSessionRemaining)
            double maxSeconds = 10.0;
            if (!isDegradedReveal && _authService is not null && _authService.IsProtectionEnabled && _authService.IsAuthorized)
            {
                var authRemaining = _authService.RemainingDuration.TotalSeconds;
                if (authRemaining < maxSeconds)
                    maxSeconds = Math.Max(0, authRemaining);
            }

            item.RevealDeadline = now.AddSeconds(maxSeconds);
            item.IsTotpCopied = false;
            EnsureTotpPresentationTimer(allAccounts);
        }
        else
        {
            ShowInfo(Loc.ErrorTitle, error ?? Loc.TotpInvalid, InfoBarSeverity.Warning);
        }
    }

    public void HideTotp(AccountItemViewModel? item, IEnumerable<AccountItemViewModel> allAccounts)
    {
        if (item is null) return;
        item.ResetTotpPresentation();
        EnsureTotpPresentationTimer(allAccounts);
    }

    public void CopyTotpCode(AccountItemViewModel? item)
    {
        if (item is null || !item.IsTotpRevealed) return;
        var rawCode = item.TotpCodeText.Replace(" ", string.Empty);
        if (string.IsNullOrEmpty(rawCode) || rawCode.Contains('\u2022')) return;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(rawCode);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            item.IsTotpCopied = true;
            _dispatcher.Enqueue(async () =>
            {
                await Task.Delay(2000);
                item.IsTotpCopied = false;
            });
        }
        catch
        {
            // Clipboard busy, ignore
        }
    }

    public async Task AddOrManageTotpAsync(AccountItemViewModel? item, IEnumerable<AccountItemViewModel> allAccounts)
    {
        if (item is null || _totpStore is null) return;

        var setupResult = await _ui.PromptTotpSetupAsync(item.DisplayName, item.HasTotpConfigured);
        if (setupResult is null) return;

        if (setupResult.Action == TotpSetupAction.SaveNewKey && !string.IsNullOrWhiteSpace(setupResult.ProvisioningKey))
        {
            try
            {
                _totpStore.Save(item.Id, setupResult.ProvisioningKey, _clock.UtcNow);
                item.HasTotpConfigured = true;
                item.ResetTotpPresentation();
                EnsureTotpPresentationTimer(allAccounts);
                ShowInfo(Loc.RefreshAllDoneTitle, Loc.TotpKeySavedLocally, InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowInfo(Loc.ErrorTitle, ex.Message, InfoBarSeverity.Error);
            }
        }
        else if (setupResult.Action == TotpSetupAction.RemoveKey)
        {
            try
            {
                _totpStore.Delete(item.Id);
                item.HasTotpConfigured = false;
                item.ResetTotpPresentation();
                EnsureTotpPresentationTimer(allAccounts);
                ShowInfo(Loc.RemovedTitle, Loc.TotpKeySavedLocally, InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                ShowInfo(Loc.ErrorTitle, ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    public void HideAll(IEnumerable<AccountItemViewModel> accounts)
    {
        foreach (var item in accounts)
        {
            if (item.IsTotpRevealed)
                item.ResetTotpPresentation();
        }
        if (_totpPresentationTimer is not null && _totpPresentationTimer.IsEnabled)
            _totpPresentationTimer.Stop();
    }

    public void ResetOnCollapse(AccountItemViewModel? item, IEnumerable<AccountItemViewModel> allAccounts)
    {
        if (item is null) return;
        item.ResetTotpPresentation();
        EnsureTotpPresentationTimer(allAccounts);
    }

    public void EnsureTotpPresentationTimer(IEnumerable<AccountItemViewModel> allAccounts)
    {
        if (_totpPresentationTimer is null)
        {
            _totpPresentationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _totpPresentationTimer.Tick += (_, _) => OnTotpPresentationTick(allAccounts);
        }

        bool anyRevealed = allAccounts.Any(a => a.IsTotpRevealed);
        if (anyRevealed && !_totpPresentationTimer.IsEnabled)
        {
            _totpPresentationTimer.Start();
        }
        else if (!anyRevealed && _totpPresentationTimer.IsEnabled)
        {
            _totpPresentationTimer.Stop();
        }
    }

    private void OnTotpPresentationTick(IEnumerable<AccountItemViewModel> allAccounts)
    {
        var now = _clock.UtcNow;
        bool anyRevealed = false;

        foreach (var item in allAccounts)
        {
            if (!item.IsTotpRevealed) continue;

            // If revealed under an authorization session that is no longer authorized, hide immediately
            if (!item.IsDegradedReveal && _authService is not null && _authService.IsProtectionEnabled && !_authService.IsAuthorized)
            {
                item.ResetTotpPresentation();
                continue;
            }

            // 1. Reveal deadline (10s auto-hide)
            if (now >= item.RevealDeadline)
            {
                item.ResetTotpPresentation();
                continue;
            }

            // 2. TOTP period countdown / rollover
            if (item.TotpSecondsRemaining <= 1)
            {
                if (_totpStore is not null && _totpStore.TryComputeCode(item.Id, now, out var nextCode, out _))
                {
                    item.TotpCodeText = nextCode.Formatted;
                    item.TotpSecondsRemaining = nextCode.SecondsRemaining;
                    item.TotpCountdownText = $"{nextCode.SecondsRemaining}s";
                }
                else
                {
                    item.ResetTotpPresentation();
                    continue;
                }
            }
            else
            {
                item.TotpSecondsRemaining--;
                item.TotpCountdownText = $"{item.TotpSecondsRemaining}s";
            }

            anyRevealed = true;
        }

        if (!anyRevealed && _totpPresentationTimer is not null && _totpPresentationTimer.IsEnabled)
        {
            _totpPresentationTimer.Stop();
        }
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        _notifications.Show(title, message, severity);
    }

    public void Dispose()
    {
        if (_totpPresentationTimer is not null)
        {
            _totpPresentationTimer.Stop();
            _totpPresentationTimer = null;
        }
    }
}
