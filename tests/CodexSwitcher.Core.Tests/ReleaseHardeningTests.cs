using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ReleaseHardeningTests
{
    #region 1. Data Root Isolation & Precedence

    [Fact]
    public void AppPaths_DefaultRoot_PointsToCodexSwitchboard()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expected = Path.Combine(localAppData, "CodexSwitchboard");
        Assert.Equal(expected, AppPaths.DefaultRoot);
    }

    [Fact]
    public void AppPaths_LegacyDefaultRoot_PointsToCodexSwitcher()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expected = Path.Combine(localAppData, "CodexSwitcher");
        Assert.Equal(expected, AppPaths.LegacyDefaultRoot);
    }

    [Fact]
    public void AppPaths_Instance_RespectsExplicitRoot()
    {
        var custom = @"C:\CustomData\Switchboard";
        var paths = new AppPaths(root: custom);
        Assert.Equal(custom, paths.Root);
        Assert.Equal(Path.Combine(custom, "vault"), paths.VaultDir);
        Assert.Equal(Path.Combine(custom, "profiles.json"), paths.ProfilesPath);
    }

    [Fact]
    public void AppPaths_Instance_RespectsExplicitLegacyRoot()
    {
        var customLegacy = @"C:\CustomData\LegacySwitcher";
        var paths = new AppPaths(legacyRoot: customLegacy);
        Assert.Equal(customLegacy, paths.LegacyRoot);
    }

    [Fact]
    public void AppPaths_EnvironmentVariable_CodexSwitchboardHome_TakesPrecedence()
    {
        var originalSwitchboard = Environment.GetEnvironmentVariable("CODEXSWITCHBOARD_HOME");
        var originalSwitcher = Environment.GetEnvironmentVariable("CODEXSWITCHER_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEXSWITCHBOARD_HOME", @"C:\TestSwitchboardHome");
            Environment.SetEnvironmentVariable("CODEXSWITCHER_HOME", @"C:\TestSwitcherHome");

            var paths = new AppPaths();
            Assert.Equal(@"C:\TestSwitchboardHome", paths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSWITCHBOARD_HOME", originalSwitchboard);
            Environment.SetEnvironmentVariable("CODEXSWITCHER_HOME", originalSwitcher);
        }
    }

    [Fact]
    public void AppPaths_EnvironmentVariable_CodexSwitcherHome_DoesNotPolluteSwitchboardRoot()
    {
        var originalSwitchboard = Environment.GetEnvironmentVariable("CODEXSWITCHBOARD_HOME");
        var originalSwitcher = Environment.GetEnvironmentVariable("CODEXSWITCHER_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEXSWITCHBOARD_HOME", null);
            Environment.SetEnvironmentVariable("CODEXSWITCHER_HOME", @"C:\TestLegacyHome");

            var paths = new AppPaths();
            // Switchboard Root MUST remain DefaultRoot (never polluted by CODEXSWITCHER_HOME)
            Assert.Equal(AppPaths.DefaultRoot, paths.Root);
            // LegacyRoot sees the legacy environment variable
            Assert.Equal(@"C:\TestLegacyHome", paths.LegacyRoot);
            // Dedicated legacy resolver also sees it
            Assert.Equal(@"C:\TestLegacyHome", LegacyDataRootResolver.ResolveLegacyRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSWITCHBOARD_HOME", originalSwitchboard);
            Environment.SetEnvironmentVariable("CODEXSWITCHER_HOME", originalSwitcher);
        }
    }

    #endregion

    #region 2. Safe Legacy Migration Service

    [Fact]
    public void LegacyMigrationService_CanMigrate_ReturnsFalse_WhenLegacyRootDoesNotExist()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("non_existent_legacy");
        var destRoot = temp.Combine("dest");

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        Assert.False(service.CanMigrate());
    }

    [Fact]
    public void LegacyMigrationService_CanMigrate_ReturnsFalse_WhenDestinationAlreadyHasProfiles()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("legacy");
        var destRoot = temp.Combine("dest");

        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "profiles.json"), "[]");

        Directory.CreateDirectory(destRoot);
        File.WriteAllText(Path.Combine(destRoot, "profiles.json"), "[]");

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        Assert.False(service.CanMigrate());
    }

    [Fact]
    public void LegacyMigrationService_CanMigrate_ReturnsFalse_WhenDestinationAlreadyHasVaultBlobs()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("legacy");
        var destRoot = temp.Combine("dest");

        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "profiles.json"), "[]");

        Directory.CreateDirectory(Path.Combine(destRoot, "vault"));
        File.WriteAllBytes(Path.Combine(destRoot, "vault", $"{Guid.NewGuid():N}.bin"), [1, 2, 3]);

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        Assert.False(service.CanMigrate());
    }

    [Fact]
    public void LegacyMigrationService_CanMigrate_ReturnsTrue_WhenLegacyHasDataAndDestinationEmpty()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("legacy");
        var destRoot = temp.Combine("dest");

        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "profiles.json"), "[]");

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        Assert.True(service.CanMigrate());
    }

    [Fact]
    public async Task LegacyMigrationService_MigrateAsync_Refuses_WhenDestinationAlreadyPopulated()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("legacy");
        var destRoot = temp.Combine("dest");

        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "profiles.json"), "[]");

        Directory.CreateDirectory(destRoot);
        File.WriteAllText(Path.Combine(destRoot, "profiles.json"), "[]");

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        var result = await service.MigrateAsync();

        Assert.False(result.Success);
        Assert.True(result.DestinationAlreadyExisted);
    }

    [Fact]
    public async Task LegacyMigrationService_MigrateAsync_Fails_WhenLegacyRootMissing()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("missing_legacy");
        var destRoot = temp.Combine("dest");

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        var result = await service.MigrateAsync();

        Assert.False(result.Success);
        Assert.True(result.NoLegacyDataFound);
    }

    [Fact]
    public async Task LegacyMigrationService_MigrateAsync_Fails_WhenProfilesJsonCorrupt()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("legacy");
        var destRoot = temp.Combine("dest");

        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "profiles.json"), "{ invalid-json ::: }");

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        var result = await service.MigrateAsync();

        Assert.False(result.Success);
        Assert.Contains("corrupt", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destRoot) && File.Exists(Path.Combine(destRoot, "profiles.json")));
    }

    [Fact]
    public async Task LegacyMigrationService_MigrateAsync_Fails_WhenVaultBlobMissing()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("legacy");
        var destRoot = temp.Combine("dest");

        var profileId = Guid.NewGuid();
        var profiles = new List<ProfileMetadata>
        {
            new() { Id = profileId, Nickname = "Work Account", AccountEmail = "work@example.com" }
        };

        Directory.CreateDirectory(legacyRoot);
        Directory.CreateDirectory(Path.Combine(legacyRoot, "vault"));
        File.WriteAllText(Path.Combine(legacyRoot, "profiles.json"), JsonSerializer.Serialize(profiles));
        // Note: vault file {profileId}.bin is missing!

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        var result = await service.MigrateAsync();

        Assert.False(result.Success);
        Assert.Contains("missing", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyMigrationService_MigrateAsync_Fails_WhenVaultBlobZeroBytes()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("legacy");
        var destRoot = temp.Combine("dest");

        var profileId = Guid.NewGuid();
        var profiles = new List<ProfileMetadata>
        {
            new() { Id = profileId, Nickname = "Work Account", AccountEmail = "work@example.com" }
        };

        Directory.CreateDirectory(legacyRoot);
        var vaultDir = Path.Combine(legacyRoot, "vault");
        Directory.CreateDirectory(vaultDir);
        File.WriteAllText(Path.Combine(legacyRoot, "profiles.json"), JsonSerializer.Serialize(profiles));
        File.WriteAllBytes(Path.Combine(vaultDir, $"{profileId}.bin"), []); // Zero bytes!

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        var result = await service.MigrateAsync();

        Assert.False(result.Success);
        Assert.Contains("zero bytes", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyMigrationService_MigrateAsync_CopiesData_And_LeavesLegacyUntouched()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("legacy");
        var destRoot = temp.Combine("dest");

        var profileId = Guid.NewGuid();
        var profiles = new List<ProfileMetadata>
        {
            new() { Id = profileId, Nickname = "Test Account", AccountEmail = "test@example.com" }
        };

        Directory.CreateDirectory(legacyRoot);
        var legacyVault = Path.Combine(legacyRoot, "vault");
        Directory.CreateDirectory(legacyVault);

        var originalProfilesJson = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        var originalBlobBytes = Encoding.UTF8.GetBytes("DPAPI_ENCRYPTED_MOCK_CIPHERTEXT_BLOB_BYTE_STREAM");

        File.WriteAllText(Path.Combine(legacyRoot, "profiles.json"), originalProfilesJson);
        File.WriteAllBytes(Path.Combine(legacyVault, $"{profileId}.bin"), originalBlobBytes);
        File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), "{\"AlwaysConfirmSwitch\":true}");

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        var result = await service.MigrateAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.MigratedProfilesCount);

        // Invariant 1: Source files remain untouched
        Assert.True(File.Exists(Path.Combine(legacyRoot, "profiles.json")));
        Assert.True(File.Exists(Path.Combine(legacyVault, $"{profileId}.bin")));
        Assert.True(File.Exists(Path.Combine(legacyRoot, "settings.json")));
        Assert.Equal(originalProfilesJson, File.ReadAllText(Path.Combine(legacyRoot, "profiles.json")));
        Assert.Equal(originalBlobBytes, File.ReadAllBytes(Path.Combine(legacyVault, $"{profileId}.bin")));

        // Invariant 2: Destination files exist and match exactly
        var destProfilesPath = Path.Combine(destRoot, "profiles.json");
        var destBlobPath = Path.Combine(destRoot, "vault", $"{profileId}.bin");
        var destSettingsPath = Path.Combine(destRoot, "settings.json");

        Assert.True(File.Exists(destProfilesPath));
        Assert.True(File.Exists(destBlobPath));
        Assert.True(File.Exists(destSettingsPath));

        Assert.Equal(originalProfilesJson, File.ReadAllText(destProfilesPath));
        Assert.Equal(originalBlobBytes, File.ReadAllBytes(destBlobPath));
    }

    [Fact]
    public async Task LegacyMigrationService_MigrateAsync_PreservesRawByteCiphertext_ForDpapi()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var legacyRoot = temp.Combine("legacy");
        var destRoot = temp.Combine("dest");

        var profileId = Guid.NewGuid();
        var profiles = new List<ProfileMetadata>
        {
            new() { Id = profileId, Nickname = "Dpapi Account", AccountEmail = "dpapi@example.com" }
        };

        // Create arbitrary binary blob simulating Windows DPAPI CryptProtectData output
        var simulatedDpapiBytes = new byte[256];
        new Random(42).NextBytes(simulatedDpapiBytes);

        Directory.CreateDirectory(legacyRoot);
        Directory.CreateDirectory(Path.Combine(legacyRoot, "vault"));
        File.WriteAllText(Path.Combine(legacyRoot, "profiles.json"), JsonSerializer.Serialize(profiles));
        File.WriteAllBytes(Path.Combine(legacyRoot, "vault", $"{profileId}.bin"), simulatedDpapiBytes);

        var service = new LegacyMigrationService(fs, legacyRoot, destRoot);
        var result = await service.MigrateAsync();

        Assert.True(result.Success);

        var destBytes = File.ReadAllBytes(Path.Combine(destRoot, "vault", $"{profileId}.bin"));
        Assert.Equal(simulatedDpapiBytes, destBytes);
    }

    #endregion

    #region 3. Runtime Settings & Resolver Validation

    [Fact]
    public void AppSettings_CodexExecutablePathOverride_RoundTrips()
    {
        var settings = new AppSettings
        {
            AlwaysConfirmSwitch = true,
            GracefulCloseTimeoutSeconds = 7,
            CodexExecutablePathOverride = @"C:\CustomBin\codex.exe"
        };

        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.AlwaysConfirmSwitch);
        Assert.Equal(7, deserialized.GracefulCloseTimeoutSeconds);
        Assert.Equal(@"C:\CustomBin\codex.exe", deserialized.CodexExecutablePathOverride);
    }

    [Fact]
    public void CodexRuntimeResolver_ValidateExecutable_ReturnsFalse_ForNonExistentPath()
    {
        var resolver = new CodexRuntimeResolver();
        var fakePath = @"C:\NonExistentDirectory\FakePath\codex.exe";

        var isValid = resolver.ValidateExecutable(fakePath, out var version, out var error);

        Assert.False(isValid);
        Assert.Null(version);
        Assert.NotNull(error);
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodexCapabilityCache_Clear_InvalidatesCachedEntries()
    {
        var cache = new CodexCapabilityCache();
        var exe = "C:\\fake\\codex.exe";
        cache.SetCapabilities(exe, CodexRuntimeCapabilities.ModernFull);
        Assert.NotNull(cache.GetCapabilities(exe));

        cache.Clear();
        Assert.Null(cache.GetCapabilities(exe));
    }

    #endregion

    #region 4. Sanitization & Release Quality Gates

    [Fact]
    public void Sanitization_SourceFiles_DoNotContain_DeveloperUsername()
    {
        var projectDir = FindSwitchboardRoot(AppContext.BaseDirectory);
        Assert.NotNull(projectDir);

        var docsDir = Path.Combine(projectDir, "docs");
        if (Directory.Exists(docsDir))
        {
            foreach (var file in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                Assert.DoesNotContain("Malenkiy" + "_Solovey", content);
            }
        }
    }

    [Fact]
    public void Sanitization_Documentation_ContainsNo_RealAccountActivityTelemetry()
    {
        var projectDir = FindSwitchboardRoot(AppContext.BaseDirectory);
        Assert.NotNull(projectDir);

        var docsDir = Path.Combine(projectDir, "docs");
        if (Directory.Exists(docsDir))
        {
            foreach (var file in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                // Verify no raw real request count leak
                Assert.DoesNotContain("186 requests", content);
                Assert.DoesNotContain("249 requests", content);
            }
        }
    }

    [Fact]
    public void ReleasePackaging_ScannerLogic_RejectsForbiddenArtifacts()
    {
        using var temp = new TempDir();
        var distDir = temp.Combine("dist_package");
        Directory.CreateDirectory(distDir);

        // Put legitimate files
        File.WriteAllText(Path.Combine(distDir, "CodexSwitcher.dll"), "dummy binary");
        File.WriteAllText(Path.Combine(distDir, "LICENSE"), "MIT License");

        Assert.Empty(ScanForForbiddenReleaseArtifacts(distDir));

        // Add forbidden codex.exe
        var forbiddenExe = Path.Combine(distDir, "codex.exe");
        File.WriteAllText(forbiddenExe, "fake codex runtime binary");

        var violations = ScanForForbiddenReleaseArtifacts(distDir);
        Assert.Contains(violations, v => v.Contains("codex.exe"));

        File.Delete(forbiddenExe);

        // Add forbidden auth.json
        var forbiddenAuth = Path.Combine(distDir, "auth.json");
        File.WriteAllText(forbiddenAuth, "{\"tokens\":{}}");

        violations = ScanForForbiddenReleaseArtifacts(distDir);
        Assert.Contains(violations, v => v.Contains("auth.json"));

        File.Delete(forbiddenAuth);

        // Add forbidden .bin DPAPI blob
        var forbiddenBin = Path.Combine(distDir, "secret.bin");
        File.WriteAllBytes(forbiddenBin, [1, 2, 3]);

        violations = ScanForForbiddenReleaseArtifacts(distDir);
        Assert.Contains(violations, v => v.Contains(".bin"));
    }

    [Fact]
    public void Branding_Assets_DoNotContain_OpenAiLogos()
    {
        var projectDir = FindSwitchboardRoot(AppContext.BaseDirectory);
        Assert.NotNull(projectDir);

        var assetsDir = Path.Combine(projectDir, "src", "CodexSwitcher.App", "Assets");
        if (Directory.Exists(assetsDir))
        {
            var files = Directory.EnumerateFiles(assetsDir, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file).ToLowerInvariant();
                Assert.DoesNotContain("openai", fileName);
                Assert.DoesNotContain("chatgpt", fileName);
            }
        }
    }

    [Fact]
    public void LegacyDataRootResolver_Resolves_Explicit_EnvVar_And_Default()
    {
        Assert.Equal(@"C:\ExplicitLegacy", LegacyDataRootResolver.ResolveLegacyRoot(@"C:\ExplicitLegacy"));

        var original = Environment.GetEnvironmentVariable("CODEXSWITCHER_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEXSWITCHER_HOME", @"C:\EnvLegacy");
            Assert.Equal(@"C:\EnvLegacy", LegacyDataRootResolver.ResolveLegacyRoot());

            Environment.SetEnvironmentVariable("CODEXSWITCHER_HOME", null);
            Assert.Equal(AppPaths.LegacyDefaultRoot, LegacyDataRootResolver.ResolveLegacyRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEXSWITCHER_HOME", original);
        }
    }

    [Fact]
    public void WebView2AvailabilityService_WhenAvailable_ReturnsTrue()
    {
        var service = new WebView2AvailabilityService(() => "125.0.2535.85");
        Assert.True(service.IsAvailable());
        Assert.Equal("125.0.2535.85", service.GetInstalledVersion());
    }

    [Fact]
    public void WebView2AvailabilityService_WhenMissing_ReturnsFalse()
    {
        var service = new WebView2AvailabilityService(() => null);
        Assert.False(service.IsAvailable());
        Assert.Null(service.GetInstalledVersion());
    }

    [Fact]
    public void WebView2AvailabilityService_WhenThrows_HandlesGracefully()
    {
        var service = new WebView2AvailabilityService(() => throw new FileNotFoundException("WebView2Loader.dll missing"));
        Assert.False(service.IsAvailable());
        Assert.Null(service.GetInstalledVersion());
    }

    [Fact]
    public void AccountTokenUsageSummary_ExposesOnly_ServerReportedFields()
    {
        var summary = new AccountTokenUsageSummary(
            LifetimeTokens: 1000000L,
            PeakDailyTokens: 50000L,
            LongestRunningTurnSeconds: 300L,
            CurrentStreakDays: 5L,
            LongestStreakDays: 14L);

        Assert.Equal(1000000L, summary.LifetimeTokens);
        Assert.Equal(50000L, summary.PeakDailyTokens);
        Assert.Equal(300L, summary.LongestRunningTurnSeconds);
        Assert.Equal(5L, summary.CurrentStreakDays);
        Assert.Equal(14L, summary.LongestStreakDays);
    }

    [Fact]
    public void DynamicQuotaWindow_ArbitraryDuration_Supported()
    {
        Assert.Equal("450m", CodexUsageResponseParser.FormatDuration(450));
        Assert.Equal("5h", CodexUsageResponseParser.FormatDuration(300));
        Assert.Equal("7d", CodexUsageResponseParser.FormatDuration(10080));
        Assert.Equal("unknown", CodexUsageResponseParser.FormatDuration(null));

        var window450m = new UsageWindowModel(
            Slot: "primary",
            DurationMinutes: 450,
            DisplayLabel: CodexUsageResponseParser.FormatDuration(450),
            UsedPercent: 50.0,
            RemainingPercent: 50.0,
            ResetsAt: null,
            IsExhausted: false,
            IsLowQuota: false);
        Assert.Equal("450m", window450m.DisplayLabel);

        var windowUnknown = new UsageWindowModel(
            Slot: "custom",
            DurationMinutes: null,
            DisplayLabel: CodexUsageResponseParser.FormatDuration(null),
            UsedPercent: 20.0,
            RemainingPercent: 80.0,
            ResetsAt: null,
            IsExhausted: false,
            IsLowQuota: false);
        Assert.Equal("unknown", windowUnknown.DisplayLabel);
    }

    [Fact]
    public void PublicDocs_Sanitization_NoNamedMutexClaim()
    {
        var projectDir = FindSwitchboardRoot(AppContext.BaseDirectory);
        Assert.NotNull(projectDir);

        var docFiles = new[]
        {
            Path.Combine(projectDir, "README.md"),
            Path.Combine(projectDir, "SECURITY.md"),
            Path.Combine(projectDir, "CHANGELOG.md"),
            Path.Combine(projectDir, "docs", "RELEASE_NOTES_0.1.0.md")
        };

        foreach (var file in docFiles)
        {
            if (File.Exists(file))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("named mutex", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("win32 mutex", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("global mutex", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void PublicDocs_Sanitization_NoInputOutputReasoningTokenClaims()
    {
        var projectDir = FindSwitchboardRoot(AppContext.BaseDirectory);
        Assert.NotNull(projectDir);

        var docFiles = new[]
        {
            Path.Combine(projectDir, "README.md"),
            Path.Combine(projectDir, "CHANGELOG.md"),
            Path.Combine(projectDir, "docs", "RELEASE_NOTES_0.1.0.md")
        };

        foreach (var file in docFiles)
        {
            if (File.Exists(file))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("input tokens", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("output tokens", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("reasoning tokens", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("request volume", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void PublicDocs_Sanitization_NoUnverifiedLatestStableCodexVersion()
    {
        var projectDir = FindSwitchboardRoot(AppContext.BaseDirectory);
        Assert.NotNull(projectDir);

        var readmePath = Path.Combine(projectDir, "README.md");
        if (File.Exists(readmePath))
        {
            var text = File.ReadAllText(readmePath);
            Assert.DoesNotContain("Latest Stable", text);
        }
    }

    [Fact]
    public void PublicDocs_ThirdPartyNotices_Contains_CodexMonitorAttributionAndGate()
    {
        var projectDir = FindSwitchboardRoot(AppContext.BaseDirectory);
        Assert.NotNull(projectDir);

        var noticesPath = Path.Combine(projectDir, "THIRD_PARTY_NOTICES.md");
        Assert.True(File.Exists(noticesPath));

        var text = File.ReadAllText(noticesPath);
        Assert.Contains("codex-monitor", text);
        Assert.Contains("NeMoSova19", text);
        Assert.Contains("PRIVATE_UPSTREAM_PUBLICATION_RIGHTS", text);
        Assert.Contains("USER_CONFIRMATION_REQUIRED", text);
    }

    [Fact]
    public void PublicDocs_Privacy_DoesNotClaim_NoExternalNetworkConnections()
    {
        var projectDir = FindSwitchboardRoot(AppContext.BaseDirectory);
        Assert.NotNull(projectDir);

        var docFiles = new[]
        {
            Path.Combine(projectDir, "README.md"),
            Path.Combine(projectDir, "PRIVACY.md")
        };

        foreach (var file in docFiles)
        {
            if (File.Exists(file))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain("no external network connections", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string? FindSwitchboardRoot(string startingDir)
    {
        var dir = new DirectoryInfo(startingDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodexSwitcher.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static List<string> ScanForForbiddenReleaseArtifacts(string directory)
    {
        var forbiddenPatterns = new[]
        {
            "codex.exe",
            "auth.json",
            "*.bin",
            "*.env",
            "id_rsa",
            "id_ed25519",
            "audit.log",
            "usage-cache.json"
        };

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            foreach (var pattern in forbiddenPatterns)
            {
                if (pattern.StartsWith("*."))
                {
                    var ext = pattern.Substring(1);
                    if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"Forbidden file extension match: {file} matches {pattern}");
                    }
                }
                else if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"Forbidden file name match: {file}");
                }
            }
        }
        return violations;
    }

    #endregion
}
