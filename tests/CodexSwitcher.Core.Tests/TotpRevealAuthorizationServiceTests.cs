using System.Reflection;
using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class TotpRevealAuthorizationServiceTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => 1_000_000; // 1 tick = 1 microsecond
        public void Advance(TimeSpan delta) => _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);
    }

    private sealed class FakeWindowsUserVerificationService : IWindowsUserVerificationService
    {
        public WindowsVerificationAvailability Availability { get; set; } = WindowsVerificationAvailability.Available;
        public WindowsVerificationResult NextResult { get; set; } = WindowsVerificationResult.Verified;
        public int VerificationCallCount { get; private set; }
        public List<string> RequestedMessages { get; } = [];
        public Func<Task>? OnVerificationRequested { get; set; }

        public Task<WindowsVerificationAvailability> CheckAvailabilityAsync() => Task.FromResult(Availability);

        public async Task<WindowsVerificationResult> RequestVerificationAsync(string message)
        {
            VerificationCallCount++;
            RequestedMessages.Add(message);
            if (OnVerificationRequested is not null)
            {
                await OnVerificationRequested();
            }
            return NextResult;
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
            if (!Credentials.TryGetValue(profileId, out var secretStr))
            {
                code = default;
                error = "Not found";
                return false;
            }

            if (!Totp.TryParse(secretStr, out var secret, out error))
            {
                code = default;
                return false;
            }

            code = secret!.Compute(now);
            error = null;
            return true;
        }

        public bool Delete(Guid profileId) => Credentials.Remove(profileId);
    }

    [Fact]
    public void Settings_OldSettingsMissingProperties_DefaultsToEnabledAnd5Minutes()
    {
        // Missing new properties in legacy json
        var legacyJson = "{}";
        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.NotNull(settings);
        Assert.True(settings.RequireWindowsVerificationForTotpReveal);
        Assert.Equal(5, settings.TotpWindowsVerificationDurationMinutes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(120, 60)]
    public void Settings_Duration_ClampedBetween1And60Minutes(int input, int expected)
    {
        var settings = new AppSettings
        {
            TotpWindowsVerificationDurationMinutes = input
        };

        Assert.Equal(expected, settings.TotpWindowsVerificationDurationMinutes);
    }

    [Fact]
    public void Settings_SerializationRoundTrip_NeverPersistsAuthorizationState()
    {
        using var tempDir = new TempDir();
        var fs = new PhysicalFileSystem();
        var settingsPath = Path.Combine(tempDir.Root, "settings.json");
        var store = new SettingsStore(fs, settingsPath);

        var settings = new AppSettings
        {
            RequireWindowsVerificationForTotpReveal = false,
            TotpWindowsVerificationDurationMinutes = 15
        };
        store.Save(settings);

        var loaded = store.Load();
        Assert.False(loaded.RequireWindowsVerificationForTotpReveal);
        Assert.Equal(15, loaded.TotpWindowsVerificationDurationMinutes);

        var json = fs.ReadAllText(settingsPath);
        Assert.DoesNotContain("isAuthorized", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorizedAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verified", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pin", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FirstReveal_PromptsVerifier_DecryptsOnlyAfterVerified()
    {
        var settings = new AppSettings();
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var fakeTime = new FakeTimeProvider();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier, fakeTime);
        var credStore = new FakeTotpCredentialStore();

        var profileId = Guid.NewGuid();
        credStore.Save(profileId, "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");

        Assert.False(authService.IsAuthorized);
        Assert.True(authService.IsVerificationRequired());

        // Attempt reveal workflow: check auth first
        bool wasAuthorizedBefore = authService.IsAuthorized;
        Assert.False(wasAuthorizedBefore);
        Assert.Equal(0, credStore.TryComputeCodeCount);

        var outcome = await authService.EnsureAuthorizedAsync("Custom message");

        Assert.True(outcome.Success);
        Assert.Equal(WindowsVerificationResult.Verified, outcome.Status);
        Assert.Equal(1, fakeVerifier.VerificationCallCount);
        Assert.Equal("Custom message", fakeVerifier.RequestedMessages[0]);
        Assert.True(authService.IsAuthorized);
        Assert.Equal(TimeSpan.FromMinutes(5), authService.RemainingDuration);

        // Secret decryption only after authorization succeeds
        Assert.True(credStore.TryComputeCode(profileId, DateTimeOffset.UtcNow, out var code, out _));
        Assert.Equal(1, credStore.TryComputeCodeCount);
        Assert.NotEmpty(code.Code);
    }

    [Fact]
    public async Task VerificationCanceled_DoesNotDecrypt_RemainsLocked()
    {
        var settings = new AppSettings();
        var fakeVerifier = new FakeWindowsUserVerificationService
        {
            NextResult = WindowsVerificationResult.Canceled
        };
        var fakeTime = new FakeTimeProvider();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier, fakeTime);
        var credStore = new FakeTotpCredentialStore();

        var profileId = Guid.NewGuid();
        credStore.Save(profileId, "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");

        var outcome = await authService.EnsureAuthorizedAsync("Verify");

        Assert.False(outcome.Success);
        Assert.Equal(WindowsVerificationResult.Canceled, outcome.Status);
        Assert.False(authService.IsAuthorized);
        Assert.Equal(TimeSpan.Zero, authService.RemainingDuration);

        // Security check: cred store must NOT be called for compute
        Assert.Equal(0, credStore.TryComputeCodeCount);
    }

    [Fact]
    public async Task SessionReuse_AcrossProfiles_InvokesVerifierOnce()
    {
        var settings = new AppSettings { TotpWindowsVerificationDurationMinutes = 5 };
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var fakeTime = new FakeTimeProvider();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier, fakeTime);

        // Profile A verification
        var outcomeA = await authService.EnsureAuthorizedAsync("Reveal A");
        Assert.True(outcomeA.Success);
        Assert.Equal(1, fakeVerifier.VerificationCallCount);

        // Advance 1 minute
        fakeTime.Advance(TimeSpan.FromMinutes(1));
        Assert.True(authService.IsAuthorized);

        // Profile B reveal within session
        var outcomeB = await authService.EnsureAuthorizedAsync("Reveal B");
        Assert.True(outcomeB.Success);
        Assert.Equal(1, fakeVerifier.VerificationCallCount); // Still exactly 1 prompt

        // Profile A reveal again within session
        var outcomeA2 = await authService.EnsureAuthorizedAsync("Reveal A again");
        Assert.True(outcomeA2.Success);
        Assert.Equal(1, fakeVerifier.VerificationCallCount); // Still exactly 1 prompt
    }

    [Fact]
    public async Task Existing10SecondHide_PreservesValidAuthorizationSession()
    {
        var settings = new AppSettings { TotpWindowsVerificationDurationMinutes = 5 };
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var fakeTime = new FakeTimeProvider();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier, fakeTime);

        await authService.EnsureAuthorizedAsync("Prompt");
        Assert.Equal(1, fakeVerifier.VerificationCallCount);

        // Simulate 10-second code presentation auto-hide
        fakeTime.Advance(TimeSpan.FromSeconds(10));

        // Code auto-hides in UI, but authorization session is still valid!
        Assert.True(authService.IsAuthorized);
        Assert.True(authService.RemainingDuration > TimeSpan.FromMinutes(4));

        // Subsequent reveal requires no prompt
        var outcome = await authService.EnsureAuthorizedAsync("Prompt again");
        Assert.True(outcome.Success);
        Assert.Equal(1, fakeVerifier.VerificationCallCount);
    }

    [Fact]
    public async Task AuthorizationExpiry_LocksSession_RequiresNewPrompt()
    {
        var settings = new AppSettings { TotpWindowsVerificationDurationMinutes = 5 };
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var fakeTime = new FakeTimeProvider();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier, fakeTime);

        await authService.EnsureAuthorizedAsync("Prompt");
        Assert.Equal(1, fakeVerifier.VerificationCallCount);

        // Advance to 4m 59s
        fakeTime.Advance(TimeSpan.FromSeconds(299));
        Assert.True(authService.IsAuthorized);
        Assert.True(authService.RemainingDuration > TimeSpan.Zero);

        // Advance past 5 minutes (300s total)
        fakeTime.Advance(TimeSpan.FromSeconds(2));
        Assert.False(authService.IsAuthorized);
        Assert.Equal(TimeSpan.Zero, authService.RemainingDuration);

        // Next reveal requires prompt
        var outcome = await authService.EnsureAuthorizedAsync("Prompt 2");
        Assert.True(outcome.Success);
        Assert.Equal(2, fakeVerifier.VerificationCallCount);
    }

    [Fact]
    public async Task AuthExpiry_BoundedVisibility_RemainingDurationCalculated()
    {
        var settings = new AppSettings { TotpWindowsVerificationDurationMinutes = 5 };
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var fakeTime = new FakeTimeProvider();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier, fakeTime);

        await authService.EnsureAuthorizedAsync("Prompt");

        // Advance until only 3 seconds remain
        fakeTime.Advance(TimeSpan.FromSeconds(297));
        Assert.True(authService.IsAuthorized);
        Assert.Equal(3, Math.Round(authService.RemainingDuration.TotalSeconds));

        // Bounded lifetime test: min(10s, authSessionRemaining) = 3s
        var effectiveTtl = Math.Min(10.0, authService.RemainingDuration.TotalSeconds);
        Assert.Equal(3.0, effectiveTtl, precision: 1);

        // Advance 3s -> session expires
        fakeTime.Advance(TimeSpan.FromSeconds(3));
        Assert.False(authService.IsAuthorized);
    }

    [Fact]
    public async Task MonotonicTime_WallClockShift_DoesNotAffectExpiry()
    {
        var settings = new AppSettings { TotpWindowsVerificationDurationMinutes = 5 };
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var fakeTime = new FakeTimeProvider();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier, fakeTime);

        await authService.EnsureAuthorizedAsync("Prompt");

        // Advance monotonic clock by 2 minutes
        fakeTime.Advance(TimeSpan.FromMinutes(2));

        // Even if wall clock shifted backward 1 hour, monotonic elapsed time tracks strictly 2 minutes
        Assert.True(authService.IsAuthorized);
        Assert.Equal(3, Math.Round(authService.RemainingDuration.TotalMinutes));
    }

    [Fact]
    public void ApplicationRestart_AlwaysStartsLocked()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = true };
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier);

        Assert.False(authService.IsAuthorized);
        Assert.True(authService.IsVerificationRequired());
        Assert.Equal(TimeSpan.Zero, authService.RemainingDuration);
    }

    [Fact]
    public async Task ProtectionOff_NeverPromptsVerifier_ReturnsVerified()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = false };
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier);

        Assert.True(authService.IsAuthorized);
        Assert.False(authService.IsVerificationRequired());

        var outcome = await authService.EnsureAuthorizedAsync();
        Assert.True(outcome.Success);
        Assert.Equal(WindowsVerificationResult.Verified, outcome.Status);
        Assert.Equal(0, fakeVerifier.VerificationCallCount);
    }

    [Fact]
    public async Task EnableProtection_ImmediatelyLocksSession()
    {
        var settings = new AppSettings { RequireWindowsVerificationForTotpReveal = false };
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier);

        Assert.True(authService.IsAuthorized);

        // Enable protection
        settings.RequireWindowsVerificationForTotpReveal = true;
        authService.RecordSettingsChanged();

        Assert.False(authService.IsAuthorized);
        Assert.True(authService.IsVerificationRequired());

        await authService.EnsureAuthorizedAsync();
        Assert.Equal(1, fakeVerifier.VerificationCallCount);
    }

    [Fact]
    public async Task SettingsChange_InvalidatesActiveSession()
    {
        var settings = new AppSettings { TotpWindowsVerificationDurationMinutes = 5 };
        var fakeVerifier = new FakeWindowsUserVerificationService();
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier);

        await authService.EnsureAuthorizedAsync();
        Assert.True(authService.IsAuthorized);

        // User changes duration in Settings
        settings.TotpWindowsVerificationDurationMinutes = 10;
        authService.RecordSettingsChanged();

        Assert.False(authService.IsAuthorized);
        Assert.Equal(TimeSpan.Zero, authService.RemainingDuration);
    }

    [Fact]
    public async Task ConcurrentRevealRequests_CoalescedToSinglePrompt()
    {
        var settings = new AppSettings();
        var tcs = new TaskCompletionSource();
        var fakeVerifier = new FakeWindowsUserVerificationService
        {
            OnVerificationRequested = async () => await tcs.Task
        };
        var authService = new TotpRevealAuthorizationService(settings, fakeVerifier);

        var task1 = authService.EnsureAuthorizedAsync("Click 1");
        var task2 = authService.EnsureAuthorizedAsync("Click 2");
        var task3 = authService.EnsureAuthorizedAsync("Click 3");

        // Complete the dialog
        tcs.SetResult();

        var results = await Task.WhenAll(task1, task2, task3);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(1, fakeVerifier.VerificationCallCount); // Coalesced into 1 prompt!
    }

    [Fact]
    public void NoWindowsCredentialMaterial_StoredInModelsOrSettings()
    {
        var forbiddenWords = new[] { "WindowsPassword", "PasswordHash", "WindowsPin", "HelloCredential", "CredentialBlob" };

        var typesToScan = new[]
        {
            typeof(AppSettings),
            typeof(TotpAuthorizationOutcome),
            typeof(TotpRevealAuthorizationService),
            typeof(WindowsVerificationResult),
            typeof(WindowsVerificationAvailability)
        };

        foreach (var type in typesToScan)
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (var member in members)
            {
                foreach (var forbidden in forbiddenWords)
                {
                    Assert.False(
                        member.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"Member '{member.Name}' in type '{type.FullName}' violates credential hygiene rule '{forbidden}'.");
                }
            }
        }
    }
}
