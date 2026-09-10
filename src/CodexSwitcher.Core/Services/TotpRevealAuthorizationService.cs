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
    private const string DisableProtectionPromptMessage = "Enter your current Windows password to disable 2FA code protection.";

    private readonly AppSettings _settings;
    private readonly IWindowsUserVerificationService _verificationService;
    private readonly IWindowsPasswordVerificationService _passwordVerificationService;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();

    private long _authorizedAtTimestamp;
    private TimeSpan _sessionDuration = TimeSpan.Zero;
    private bool _isSessionActive;
    private Task<TotpAuthorizationOutcome>? _inFlightVerification;

    private sealed class UnsupportedPasswordVerificationService : IWindowsPasswordVerificationService
    {
        public bool IsSupported => false;
        public Task<WindowsPasswordVerificationResult> VerifyCurrentUserPasswordAsync(string? message = null, string? caption = null) =>
            Task.FromResult(WindowsPasswordVerificationResult.CredentialProviderUnavailable);
    }

    public TotpRevealAuthorizationService(
        AppSettings settings,
        IWindowsUserVerificationService verificationService,
        TimeProvider? timeProvider = null)
        : this(settings, verificationService, new UnsupportedPasswordVerificationService(), timeProvider)
    {
    }

    public TotpRevealAuthorizationService(
        AppSettings settings,
        IWindowsUserVerificationService verificationService,
        IWindowsPasswordVerificationService passwordVerificationService,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _verificationService = verificationService ?? throw new ArgumentNullException(nameof(verificationService));
        _passwordVerificationService = passwordVerificationService ?? throw new ArgumentNullException(nameof(passwordVerificationService));
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
        var status = await GetVerificationMethodsStatusAsync().ConfigureAwait(false);
        return status.ProtectionState;
    }

    public async Task<TotpVerificationMethodsStatus> GetVerificationMethodsStatusAsync()
    {
        if (!_settings.RequireWindowsVerificationForTotpReveal)
        {
            return new TotpVerificationMethodsStatus(
                false,
                WindowsVerificationAvailability.NotConfiguredForUser,
                _passwordVerificationService.IsSupported,
                EffectiveTotpProtectionState.DisabledByUser);
        }

        WindowsVerificationAvailability availability;
        try
        {
            availability = await _verificationService.CheckAvailabilityAsync().ConfigureAwait(false);
        }
        catch
        {
            availability = WindowsVerificationAvailability.Unknown;
        }

        bool isPasswordSupported = _passwordVerificationService.IsSupported;

        EffectiveTotpProtectionState state;
        if (availability == WindowsVerificationAvailability.Available || isPasswordSupported)
        {
            state = EffectiveTotpProtectionState.Ready;
        }
        else if (availability == WindowsVerificationAvailability.DeviceBusy)
        {
            state = EffectiveTotpProtectionState.TemporarilyUnavailable;
        }
        else
        {
            state = EffectiveTotpProtectionState.DegradedUnavailable;
        }

        return new TotpVerificationMethodsStatus(
            true,
            availability,
            isPasswordSupported,
            state);
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
            // 1. Proba disponibilidade do Hello/PIN
            WindowsVerificationAvailability helloAvailability;
            try
            {
                helloAvailability = await _verificationService.CheckAvailabilityAsync().ConfigureAwait(false);
            }
            catch
            {
                helloAvailability = WindowsVerificationAvailability.Unknown;
            }

            // Caso A: Windows Hello/PIN Disponível
            if (helloAvailability == WindowsVerificationAvailability.Available)
            {
                var helloResult = await _verificationService.RequestVerificationAsync(prompt).ConfigureAwait(false);
                if (helloResult == WindowsVerificationResult.Verified)
                {
                    StartAuthorizationSession();
                    return new TotpAuthorizationOutcome(true, helloResult, TotpAuthorizationAction.Authorized, helloAvailability);
                }
                else if (helloResult == WindowsVerificationResult.Canceled)
                {
                    // Cancelamento intencional do usuário: BLOQUEIA sem fallback
                    lock (_sync) { _isSessionActive = false; }
                    return new TotpAuthorizationOutcome(false, helloResult, TotpAuthorizationAction.Canceled, helloAvailability);
                }
                else if (helloResult is WindowsVerificationResult.RetriesExhausted or WindowsVerificationResult.Failed)
                {
                    // Falha intencional de autenticação: BLOQUEIA sem fallback
                    lock (_sync) { _isSessionActive = false; }
                    return new TotpAuthorizationOutcome(false, helloResult, TotpAuthorizationAction.Failed, helloAvailability);
                }
                else if (helloResult == WindowsVerificationResult.DeviceBusy)
                {
                    // Hello ocupado: tenta senha se suportada
                    if (_passwordVerificationService.IsSupported)
                    {
                        return await AttemptPasswordVerificationAsync(prompt, helloAvailability).ConfigureAwait(false);
                    }

                    lock (_sync) { _isSessionActive = false; }
                    return new TotpAuthorizationOutcome(false, helloResult, TotpAuthorizationAction.TemporarilyUnavailable, helloAvailability);
                }
                // Outro erro transitório: tenta caminho de senha
            }

            // Caso B: Hello indisponível (DeviceNotPresent, NotConfiguredForUser, etc.)
            if (_passwordVerificationService.IsSupported)
            {
                return await AttemptPasswordVerificationAsync(prompt, helloAvailability).ConfigureAwait(false);
            }

            // Caso C: Ambos Hello e Senha indisponíveis permanentemente neste PC/usuário
            if (helloAvailability is WindowsVerificationAvailability.DeviceNotPresent or
                               WindowsVerificationAvailability.NotConfiguredForUser or
                               WindowsVerificationAvailability.DisabledByPolicy or
                               WindowsVerificationAvailability.UnsupportedOperatingSystem)
            {
                var mappedResult = helloAvailability switch
                {
                    WindowsVerificationAvailability.NotConfiguredForUser => WindowsVerificationResult.NotConfigured,
                    WindowsVerificationAvailability.DisabledByPolicy => WindowsVerificationResult.DisabledByPolicy,
                    WindowsVerificationAvailability.UnsupportedOperatingSystem => WindowsVerificationResult.UnsupportedOperatingSystem,
                    _ => WindowsVerificationResult.NotAvailable
                };

                lock (_sync)
                {
                    _isSessionActive = false;
                    return new TotpAuthorizationOutcome(
                        true,
                        mappedResult,
                        TotpAuthorizationAction.DegradedFallback,
                        helloAvailability);
                }
            }

            // Caso D: Temporariamente indisponível (DeviceBusy ou Unknown/exceção)
            lock (_sync)
            {
                _isSessionActive = false;
                var res = helloAvailability == WindowsVerificationAvailability.DeviceBusy
                    ? WindowsVerificationResult.DeviceBusy
                    : WindowsVerificationResult.Failed;
                return new TotpAuthorizationOutcome(
                    false,
                    res,
                    TotpAuthorizationAction.TemporarilyUnavailable,
                    helloAvailability);
            }
        }
        catch
        {
            lock (_sync) { _isSessionActive = false; }
            return new TotpAuthorizationOutcome(
                false,
                WindowsVerificationResult.Failed,
                TotpAuthorizationAction.TemporarilyUnavailable,
                WindowsVerificationAvailability.Unknown);
        }
        finally
        {
            lock (_sync)
            {
                _inFlightVerification = null;
            }
        }
    }

    private async Task<TotpAuthorizationOutcome> AttemptPasswordVerificationAsync(string prompt, WindowsVerificationAvailability helloAvailability)
    {
        var passwordResult = await _passwordVerificationService.VerifyCurrentUserPasswordAsync(prompt).ConfigureAwait(false);

        switch (passwordResult)
        {
            case WindowsPasswordVerificationResult.VerifiedCurrentUser:
                StartAuthorizationSession();
                return new TotpAuthorizationOutcome(
                    true,
                    WindowsVerificationResult.Verified,
                    TotpAuthorizationAction.Authorized,
                    helloAvailability,
                    passwordResult);

            case WindowsPasswordVerificationResult.Canceled:
                lock (_sync) { _isSessionActive = false; }
                return new TotpAuthorizationOutcome(
                    false,
                    WindowsVerificationResult.Canceled,
                    TotpAuthorizationAction.Canceled,
                    helloAvailability,
                    passwordResult);

            case WindowsPasswordVerificationResult.InvalidCredentials:
            case WindowsPasswordVerificationResult.DifferentUser:
            case WindowsPasswordVerificationResult.AccountLocked:
            case WindowsPasswordVerificationResult.PasswordExpired:
            case WindowsPasswordVerificationResult.AccountRestricted:
                // Falha de autenticação do usuário: BLOQUEIA sem bypass de emergência
                lock (_sync) { _isSessionActive = false; }
                return new TotpAuthorizationOutcome(
                    false,
                    WindowsVerificationResult.Failed,
                    TotpAuthorizationAction.Failed,
                    helloAvailability,
                    passwordResult);

            case WindowsPasswordVerificationResult.CredentialProviderUnavailable:
            case WindowsPasswordVerificationResult.UnsupportedCredentialType:
            case WindowsPasswordVerificationResult.UnsupportedAuthenticationPackage:
            case WindowsPasswordVerificationResult.IdentityMappingFailed:
            case WindowsPasswordVerificationResult.NoLogonServers:
            case WindowsPasswordVerificationResult.SystemError:
            default:
                // Falha técnica de infraestrutura do CredUI / provedor de senha
                // Invariante de anti-bloqueio: oferece fallback de emergência
                lock (_sync) { _isSessionActive = false; }
                return new TotpAuthorizationOutcome(
                    false,
                    WindowsVerificationResult.Failed,
                    TotpAuthorizationAction.TemporarilyUnavailable,
                    helloAvailability,
                    passwordResult);
        }
    }

    public async Task<bool> VerifyPasswordToDisableProtectionAsync(string? message = null)
    {
        // HARD REQUIREMENT: Desativar a proteção exige SEMPRE a senha do Windows do usuário atual,
        // mesmo se houver uma sessão ativa de autorização.
        if (!_passwordVerificationService.IsSupported)
        {
            return false;
        }

        var result = await _passwordVerificationService.VerifyCurrentUserPasswordAsync(
            message ?? DisableProtectionPromptMessage).ConfigureAwait(false);

        if (result == WindowsPasswordVerificationResult.VerifiedCurrentUser)
        {
            Invalidate();
            return true;
        }

        return false;
    }

    private void StartAuthorizationSession()
    {
        lock (_sync)
        {
            _sessionDuration = TimeSpan.FromMinutes(_settings.TotpWindowsVerificationDurationMinutes);
            _authorizedAtTimestamp = _timeProvider.GetTimestamp();
            _isSessionActive = true;
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
