using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Implementação em memória do controle de autorização de revelação de códigos 2FA.
/// O estado de autorização nunca é gravado em disco.
/// </summary>
public sealed class TotpRevealAuthorizationService : ITotpRevealAuthorizationService
{
    private const string DefaultPromptMessage = "Verify your Windows identity to reveal Codex Switchboard 2FA codes.";

    private readonly AppSettings _settings;
    private readonly IWindowsUserVerificationService _verificationService;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    private long _authorizedAtTimestamp;
    private TimeSpan _sessionDuration = TimeSpan.Zero;
    private bool _isSessionActive;
    private Task<TotpAuthorizationOutcome>? _inFlightVerification;

    public TotpRevealAuthorizationService(
        AppSettings settings,
        IWindowsUserVerificationService verificationService,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _verificationService = verificationService ?? throw new ArgumentNullException(nameof(verificationService));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsProtectionEnabled => _settings.RequireWindowsVerificationForTotpReveal;

    public bool IsAuthorized
    {
        get
        {
            if (!_settings.RequireWindowsVerificationForTotpReveal)
                return true;

            lock (_sync)
            {
                if (!_isSessionActive)
                    return false;

                return RemainingDuration > TimeSpan.Zero;
            }
        }
    }

    public TimeSpan RemainingDuration
    {
        get
        {
            lock (_sync)
            {
                if (!_isSessionActive)
                    return TimeSpan.Zero;

                var elapsed = _timeProvider.GetElapsedTime(_authorizedAtTimestamp);
                var remaining = _sessionDuration - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    _isSessionActive = false;
                    return TimeSpan.Zero;
                }

                return remaining;
            }
        }
    }

    public bool IsVerificationRequired()
    {
        return _settings.RequireWindowsVerificationForTotpReveal && !IsAuthorized;
    }

    public async Task<TotpAuthorizationOutcome> EnsureAuthorizedAsync(string? message = null)
    {
        if (!_settings.RequireWindowsVerificationForTotpReveal)
            return new TotpAuthorizationOutcome(true, WindowsVerificationResult.Verified);

        Task<TotpAuthorizationOutcome> taskToAwait;
        lock (_sync)
        {
            if (_isSessionActive && RemainingDuration > TimeSpan.Zero)
            {
                return new TotpAuthorizationOutcome(true, WindowsVerificationResult.Verified);
            }

            if (_inFlightVerification is not null && !_inFlightVerification.IsCompleted)
            {
                taskToAwait = _inFlightVerification;
            }
            else
            {
                _inFlightVerification = RequestAndAuthorizeAsync(message ?? DefaultPromptMessage);
                taskToAwait = _inFlightVerification;
            }
        }

        return await taskToAwait.ConfigureAwait(false);
    }

    private async Task<TotpAuthorizationOutcome> RequestAndAuthorizeAsync(string prompt)
    {
        try
        {
            var result = await _verificationService.RequestVerificationAsync(prompt).ConfigureAwait(false);
            lock (_sync)
            {
                if (result == WindowsVerificationResult.Verified)
                {
                    _sessionDuration = TimeSpan.FromMinutes(_settings.TotpWindowsVerificationDurationMinutes);
                    _authorizedAtTimestamp = _timeProvider.GetTimestamp();
                    _isSessionActive = true;
                    return new TotpAuthorizationOutcome(true, result);
                }
                else
                {
                    _isSessionActive = false;
                    return new TotpAuthorizationOutcome(false, result);
                }
            }
        }
        catch
        {
            lock (_sync)
            {
                _isSessionActive = false;
            }
            return new TotpAuthorizationOutcome(false, WindowsVerificationResult.Failed);
        }
        finally
        {
            lock (_sync)
            {
                _inFlightVerification = null;
            }
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _isSessionActive = false;
            _authorizedAtTimestamp = 0;
            _sessionDuration = TimeSpan.Zero;
        }
    }

    public void RecordSettingsChanged()
    {
        Invalidate();
    }
}
