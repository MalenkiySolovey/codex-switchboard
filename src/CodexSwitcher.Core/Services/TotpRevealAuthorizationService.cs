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

    public async Task<EffectiveTotpProtectionState> GetEffectiveProtectionStateAsync()
    {
        if (!_settings.RequireWindowsVerificationForTotpReveal)
            return EffectiveTotpProtectionState.DisabledByUser;

        try
        {
            var availability = await _verificationService.CheckAvailabilityAsync().ConfigureAwait(false);
            return availability switch
            {
                WindowsVerificationAvailability.Available => EffectiveTotpProtectionState.Ready,
                WindowsVerificationAvailability.DeviceBusy => EffectiveTotpProtectionState.TemporarilyUnavailable,
                WindowsVerificationAvailability.Unknown => EffectiveTotpProtectionState.TemporarilyUnavailable,
                _ => EffectiveTotpProtectionState.DegradedUnavailable
            };
        }
        catch
        {
            return EffectiveTotpProtectionState.TemporarilyUnavailable;
        }
    }

    public async Task<TotpAuthorizationOutcome> EnsureAuthorizedAsync(string? message = null)
    {
        if (!_settings.RequireWindowsVerificationForTotpReveal)
            return new TotpAuthorizationOutcome(true, WindowsVerificationResult.Verified, TotpAuthorizationAction.Authorized, WindowsVerificationAvailability.Available);

        Task<TotpAuthorizationOutcome> taskToAwait;
        lock (_sync)
        {
            if (_isSessionActive && RemainingDuration > TimeSpan.Zero)
            {
                return new TotpAuthorizationOutcome(true, WindowsVerificationResult.Verified, TotpAuthorizationAction.Authorized, WindowsVerificationAvailability.Available);
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
            // Fresh probe on reveal (Section 18: Recheck availability on reveal)
            var availability = await _verificationService.CheckAvailabilityAsync().ConfigureAwait(false);

            if (availability == WindowsVerificationAvailability.Available)
            {
                var result = await _verificationService.RequestVerificationAsync(prompt).ConfigureAwait(false);
                lock (_sync)
                {
                    if (result == WindowsVerificationResult.Verified)
                    {
                        _sessionDuration = TimeSpan.FromMinutes(_settings.TotpWindowsVerificationDurationMinutes);
                        _authorizedAtTimestamp = _timeProvider.GetTimestamp();
                        _isSessionActive = true;
                        return new TotpAuthorizationOutcome(true, result, TotpAuthorizationAction.Authorized, availability);
                    }
                    else if (result == WindowsVerificationResult.Canceled)
                    {
                        _isSessionActive = false;
                        return new TotpAuthorizationOutcome(false, result, TotpAuthorizationAction.Canceled, availability);
                    }
                    else if (result is WindowsVerificationResult.RetriesExhausted or WindowsVerificationResult.Failed)
                    {
                        _isSessionActive = false;
                        return new TotpAuthorizationOutcome(false, result, TotpAuthorizationAction.Failed, availability);
                    }
                    else if (result == WindowsVerificationResult.DeviceBusy)
                    {
                        _isSessionActive = false;
                        return new TotpAuthorizationOutcome(false, result, TotpAuthorizationAction.TemporarilyUnavailable, WindowsVerificationAvailability.DeviceBusy);
                    }
                    else
                    {
                        // Verifier reported NotAvailable / NotConfigured / DisabledByPolicy / Unsupported during call
                        _isSessionActive = false;
                        return new TotpAuthorizationOutcome(true, result, TotpAuthorizationAction.DegradedFallback, availability);
                    }
                }
            }

            // Permanently unavailable on this PC/user: Degraded Fallback (Section 7 Case C)
            if (availability is WindowsVerificationAvailability.DeviceNotPresent or
                               WindowsVerificationAvailability.NotConfiguredForUser or
                               WindowsVerificationAvailability.DisabledByPolicy or
                               WindowsVerificationAvailability.UnsupportedOperatingSystem)
            {
                var mappedResult = availability switch
                {
                    WindowsVerificationAvailability.NotConfiguredForUser => WindowsVerificationResult.NotConfigured,
                    WindowsVerificationAvailability.DisabledByPolicy => WindowsVerificationResult.DisabledByPolicy,
                    WindowsVerificationAvailability.UnsupportedOperatingSystem => WindowsVerificationResult.UnsupportedOperatingSystem,
                    _ => WindowsVerificationResult.NotAvailable
                };

                lock (_sync)
                {
                    _isSessionActive = false;
                    // Do NOT start authorization session!
                    return new TotpAuthorizationOutcome(true, mappedResult, TotpAuthorizationAction.DegradedFallback, availability);
                }
            }

            // Temporarily unavailable (DeviceBusy or Unknown) (Section 7 Case D)
            lock (_sync)
            {
                _isSessionActive = false;
                var res = availability == WindowsVerificationAvailability.DeviceBusy
                    ? WindowsVerificationResult.DeviceBusy
                    : WindowsVerificationResult.Failed;
                return new TotpAuthorizationOutcome(false, res, TotpAuthorizationAction.TemporarilyUnavailable, availability);
            }
        }
        catch
        {
            lock (_sync)
            {
                _isSessionActive = false;
            }
            return new TotpAuthorizationOutcome(false, WindowsVerificationResult.Failed, TotpAuthorizationAction.TemporarilyUnavailable, WindowsVerificationAvailability.Unknown);
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
