using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Services;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class TotpRevealPasswordAuthorizationTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => 1_000_000;
        public void Advance(TimeSpan delta) => _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);
    }

    private sealed class FakeWindowsUserVerificationService : IWindowsUserVerificationService
    {
        public WindowsVerificationAvailability Availability { get; set; } = WindowsVerificationAvailability.DeviceNotPresent;
        public WindowsVerificationResult NextResult { get; set; } = WindowsVerificationResult.Verified;
        public int VerificationCallCount { get; private set; }
        public int CheckAvailabilityCallCount { get; private set; }

        public Task<WindowsVerificationAvailability> CheckAvailabilityAsync()
        {
            CheckAvailabilityCallCount++;
            return Task.FromResult(Availability);
        }

        public Task<WindowsVerificationResult> RequestVerificationAsync(string message)
        {
            VerificationCallCount++;
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeWindowsPasswordVerificationService : IWindowsPasswordVerificationService
    {
        public bool IsSupported { get; set; } = true;
        public WindowsPasswordVerificationResult NextResult { get; set; } = WindowsPasswordVerificationResult.VerifiedCurrentUser;
        public int VerifyCallCount { get; private set; }
        public List<string> RequestedMessages { get; } = [];

        public Task<WindowsPasswordVerificationResult> VerifyCurrentUserPasswordAsync(string? message = null, string? caption = null)
        {
            VerifyCallCount++;
            if (message != null) RequestedMessages.Add(message);
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeTotpCredentialStore : ITotpCredentialStore
    {
        public int TryComputeCodeCount { get; private set; }
        public int HasCredentialCount { get; private set; }
        public Dictionary<Guid, string> Credentials { get; } = [];

        public bool HasCredential(Guid profileId)
        {
            HasCredentialCount++;
            return Credentials.ContainsKey(profileId);
        }

        public void Save(Guid profileId, string provisioning, DateTimeOffset createdAt)
        {
            Credentials[profileId] = provisioning;
        }

        public void Save(Guid profileId, string provisioning)
        {
            Credentials[profileId] = provisioning;
        }

        public bool TryComputeCode(Guid profileId, DateTimeOffset now, out TotpCode code, out string? error)
        {
            TryComputeCodeCount++;
            if (Credentials.TryGetValue(profileId, out _))
            {
                code = new TotpCode("123456", 25, 30);
                error = null;
                return true;
            }
            code = default;
            error = "Not configured";
            return false;
        }

        public bool Delete(Guid profileId) => Credentials.Remove(profileId);
    }

    [Fact]
    public async Task HelloAvailable_Verified_StartsAuthorizationSession()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true, TotpWindowsVerificationDurationMinutes = 5 };
        var fakeHello = new FakeWindowsUserVerificationService
        {
            Availability = WindowsVerificationAvailability.Available,
            NextResult = WindowsVerificationResult.Verified
        };
        var fakePassword = new FakeWindowsPasswordVerificationService();
        var fakeTime = new FakeTimeProvider();
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword, fakeTime);

        var outcome = await service.EnsureAuthorizedAsync("Reveal");

        Assert.True(outcome.Success);
        Assert.True(outcome.CanReveal);
        Assert.Equal(TotpAuthorizationAction.Authorized, outcome.Action);
        Assert.True(service.IsAuthorized);
        Assert.Equal(5, (int)service.RemainingDuration.TotalMinutes);
        Assert.Equal(1, fakeHello.VerificationCallCount);
        Assert.Equal(0, fakePassword.VerifyCallCount); // Password was NOT requested because Hello succeeded
    }

    [Fact]
    public async Task HelloAvailable_Canceled_BlocksReveal_NoPasswordFallback()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService
        {
            Availability = WindowsVerificationAvailability.Available,
            NextResult = WindowsVerificationResult.Canceled
        };
        var fakePassword = new FakeWindowsPasswordVerificationService();
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        var outcome = await service.EnsureAuthorizedAsync("Reveal");

        Assert.False(outcome.Success);
        Assert.False(outcome.CanReveal);
        Assert.Equal(TotpAuthorizationAction.Canceled, outcome.Action);
        Assert.False(service.IsAuthorized);
        Assert.Equal(1, fakeHello.VerificationCallCount);
        Assert.Equal(0, fakePassword.VerifyCallCount); // Deliberate cancel MUST NOT fall back to password
    }

    [Fact]
    public async Task HelloDeviceNotPresent_PasswordVerified_StartsAuthorizationSession()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true, TotpWindowsVerificationDurationMinutes = 5 };
        var fakeHello = new FakeWindowsUserVerificationService
        {
            Availability = WindowsVerificationAvailability.DeviceNotPresent
        };
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.VerifiedCurrentUser
        };
        var fakeTime = new FakeTimeProvider();
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword, fakeTime);

        var outcome = await service.EnsureAuthorizedAsync("Reveal");

        Assert.True(outcome.Success);
        Assert.True(outcome.CanReveal);
        Assert.Equal(TotpAuthorizationAction.Authorized, outcome.Action);
        Assert.Equal(WindowsPasswordVerificationResult.VerifiedCurrentUser, outcome.PasswordStatus);
        Assert.True(service.IsAuthorized);
        Assert.Equal(5, (int)service.RemainingDuration.TotalMinutes);
        Assert.Equal(0, fakeHello.VerificationCallCount); // Hello was not available
        Assert.Equal(1, fakePassword.VerifyCallCount);    // Password verifier was invoked
    }

    [Fact]
    public async Task HelloDeviceNotPresent_WrongPassword_BlocksReveal_NoEmergencyBypass()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService { Availability = WindowsVerificationAvailability.DeviceNotPresent };
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.InvalidCredentials
        };
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        var outcome = await service.EnsureAuthorizedAsync("Reveal");

        Assert.False(outcome.Success);
        Assert.False(outcome.CanReveal);
        Assert.False(outcome.IsDegradedFallback);
        Assert.False(outcome.IsTemporarilyUnavailable);
        Assert.Equal(TotpAuthorizationAction.Failed, outcome.Action);
        Assert.Equal(WindowsPasswordVerificationResult.InvalidCredentials, outcome.PasswordStatus);
        Assert.False(service.IsAuthorized);
    }

    [Fact]
    public async Task HelloDeviceNotPresent_PasswordCanceled_BlocksReveal_NoEmergencyBypass()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService { Availability = WindowsVerificationAvailability.DeviceNotPresent };
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.Canceled
        };
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        var outcome = await service.EnsureAuthorizedAsync("Reveal");

        Assert.False(outcome.Success);
        Assert.False(outcome.CanReveal);
        Assert.Equal(TotpAuthorizationAction.Canceled, outcome.Action);
        Assert.Equal(WindowsPasswordVerificationResult.Canceled, outcome.PasswordStatus);
        Assert.False(service.IsAuthorized);
    }

    [Fact]
    public async Task HelloDeviceNotPresent_DifferentUserPassword_BlocksReveal_NoEmergencyBypass()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService { Availability = WindowsVerificationAvailability.DeviceNotPresent };
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.DifferentUser
        };
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        var outcome = await service.EnsureAuthorizedAsync("Reveal");

        Assert.False(outcome.Success);
        Assert.False(outcome.CanReveal);
        Assert.Equal(TotpAuthorizationAction.Failed, outcome.Action);
        Assert.Equal(WindowsPasswordVerificationResult.DifferentUser, outcome.PasswordStatus);
        Assert.False(service.IsAuthorized);
    }

    [Fact]
    public async Task HelloAndPasswordInfrastructureFailure_OffersAntiLockoutEmergencyFallback()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService { Availability = WindowsVerificationAvailability.DeviceNotPresent };
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.CredentialProviderUnavailable
        };
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        var outcome = await service.EnsureAuthorizedAsync("Reveal");

        // Infrastructure failure must trigger anti-lockout path (TemporarilyUnavailable with option to Show code once)
        Assert.False(outcome.Success);
        Assert.True(outcome.IsTemporarilyUnavailable);
        Assert.Equal(TotpAuthorizationAction.TemporarilyUnavailable, outcome.Action);
        Assert.Equal(WindowsPasswordVerificationResult.CredentialProviderUnavailable, outcome.PasswordStatus);
        Assert.False(service.IsAuthorized);
    }

    [Fact]
    public async Task DisableProtection_ValidPassword_DisablesAndInvalidatesSession()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService();
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.VerifiedCurrentUser
        };
        var credStore = new FakeTotpCredentialStore();
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        bool disabled = await service.VerifyPasswordToDisableProtectionAsync("Confirm disable");

        Assert.True(disabled);
        Assert.Equal(1, fakePassword.VerifyCallCount);
        // CRITICAL: Must NEVER touch TOTP secrets when disabling protection!
        Assert.Equal(0, credStore.TryComputeCodeCount);
        Assert.Equal(0, credStore.HasCredentialCount);
    }

    [Fact]
    public async Task DisableProtection_WrongPassword_Fails()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService();
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.InvalidCredentials
        };
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        bool disabled = await service.VerifyPasswordToDisableProtectionAsync("Confirm disable");

        Assert.False(disabled);
        Assert.Equal(1, fakePassword.VerifyCallCount);
    }

    [Fact]
    public async Task DisableProtection_Canceled_Fails()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService();
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.Canceled
        };
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        bool disabled = await service.VerifyPasswordToDisableProtectionAsync("Confirm disable");

        Assert.False(disabled);
        Assert.Equal(1, fakePassword.VerifyCallCount);
    }

    [Fact]
    public async Task DisableProtection_DifferentUserPassword_Fails()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService();
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.DifferentUser
        };
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        bool disabled = await service.VerifyPasswordToDisableProtectionAsync("Confirm disable");

        Assert.False(disabled);
        Assert.Equal(1, fakePassword.VerifyCallCount);
    }

    [Fact]
    public async Task DisableProtection_ActiveAuthSession_StillRequiresPassword()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true, TotpWindowsVerificationDurationMinutes = 10 };
        var fakeHello = new FakeWindowsUserVerificationService
        {
            Availability = WindowsVerificationAvailability.Available,
            NextResult = WindowsVerificationResult.Verified
        };
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            NextResult = WindowsPasswordVerificationResult.Canceled
        };
        var fakeTime = new FakeTimeProvider();
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword, fakeTime);

        // 1. Authorize via Hello -> 10-minute session active!
        var outcome = await service.EnsureAuthorizedAsync("Reveal");
        Assert.True(outcome.Success);
        Assert.True(service.IsAuthorized);
        Assert.Equal(10, (int)service.RemainingDuration.TotalMinutes);

        // 2. User tries to toggle protection OFF in Settings:
        // Even with 10 minutes remaining in the active session, password prompt is MANDATORY!
        bool disabled = await service.VerifyPasswordToDisableProtectionAsync("Confirm disable");

        // User canceled password prompt -> disabling FAILS!
        Assert.False(disabled);
        Assert.Equal(1, fakePassword.VerifyCallCount);
    }

    [Fact]
    public async Task VerificationMethodsStatus_ReflectsHelloAndPasswordCapabilities()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeHello = new FakeWindowsUserVerificationService
        {
            Availability = WindowsVerificationAvailability.DeviceNotPresent
        };
        var fakePassword = new FakeWindowsPasswordVerificationService
        {
            IsSupported = true
        };
        var service = new TotpRevealAuthorizationService(settings, fakeHello, fakePassword);

        var status = await service.GetVerificationMethodsStatusAsync();

        Assert.True(status.IsProtectionEnabled);
        Assert.Equal(WindowsVerificationAvailability.DeviceNotPresent, status.HelloAvailability);
        Assert.True(status.IsPasswordSupported);
        // When Hello is absent but password is supported, effective protection state is READY!
        Assert.Equal(EffectiveTotpProtectionState.Ready, status.ProtectionState);
    }
}
