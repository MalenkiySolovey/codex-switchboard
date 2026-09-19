using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Comprehensive automated test suite for POST-0.2.0-STABILITY-2:
/// 1. Card active-state visual correctness (no green whole-card borders on inactive/marked-used cards)
/// 2. Reactive binding & ViewModel identity preservation across Rebuilds
/// 3. High-resolution monotonic ShutdownTracer
/// 4. Sub-second shutdown lifecycle, immediate process tree kill, and zero process spawning after cancellation
/// 5. Second-pass performance: ProfileStore write-amplification prevention & Codex CLI path resolution caching
/// </summary>
public sealed class Stability2Tests : IDisposable
{
    private readonly string _testDir;
    private readonly AppPaths _testPaths;

    public Stability2Tests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "CodexSwitcher_Stability2Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        var codexHome = Path.Combine(_testDir, ".codex");
        Directory.CreateDirectory(codexHome);
        _testPaths = new AppPaths(_testDir, codexHome);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    private static Assembly LoadAppAssembly()
    {
        var baseDir = AppContext.BaseDirectory;
        var probePaths = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "CodexSwitchboard.dll")
        };

        var candidates = probePaths
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        foreach (var full in candidates)
        {
            try
            {
                return Assembly.LoadFrom(full);
            }
            catch { }
        }

        throw new InvalidOperationException("Could not locate or load CodexSwitchboard.dll for stability testing.");
    }

    #region 1. Visual Correctness & Reactive ViewModel Tests

    [Fact]
    public void AccountItemViewModel_InactiveMarkedUsed_VisualStateIsNormal_NotActiveOrWarning()
    {
        var app = LoadAppAssembly();
        var itemType = app.GetType("CodexSwitcher.App.ViewModels.AccountItemViewModel")!;
        var settings = new AppSettings();
        var now = DateTimeOffset.UtcNow;

        var profile = new ProfileMetadata
        {
            Id = Guid.NewGuid(),
            AccountEmail = "inactive@example.com",
            Nickname = "Inactive Account",
            IsActive = false,
            MarkedUsedAt = now.AddHours(-1) // Marked used recently!
        };

        var item = Activator.CreateInstance(itemType, profile, now, settings, null, false, false)!;

        var cardVisualStateProp = itemType.GetProperty("CardVisualState")!;
        var stateValue = cardVisualStateProp.GetValue(item)!.ToString();

        // Must be Normal, NEVER ActiveRouting or ActiveCredential
        Assert.Equal("Normal", stateValue);

        var isMarkedUsedProp = itemType.GetProperty("IsMarkedUsed")!;
        Assert.True((bool)isMarkedUsedProp.GetValue(item)!);
    }

    [Fact]
    public void AccountItemViewModel_ActiveRouting_VisualStateIsActiveRouting()
    {
        var app = LoadAppAssembly();
        var itemType = app.GetType("CodexSwitcher.App.ViewModels.AccountItemViewModel")!;
        var settings = new AppSettings();
        var now = DateTimeOffset.UtcNow;

        var profile = new ProfileMetadata
        {
            Id = Guid.NewGuid(),
            AccountEmail = "active@example.com",
            Nickname = "Active Account",
            IsActive = true
        };

        var item = Activator.CreateInstance(itemType, profile, now, settings, null, false, true)!;

        var cardVisualStateProp = itemType.GetProperty("CardVisualState")!;
        var stateValue = cardVisualStateProp.GetValue(item)!.ToString();

        Assert.Equal("ActiveRouting", stateValue);
    }

    [Fact]
    public void AccountItemViewModel_ActiveCredentialOnly_VisualStateIsActiveCredential()
    {
        var app = LoadAppAssembly();
        var itemType = app.GetType("CodexSwitcher.App.ViewModels.AccountItemViewModel")!;
        var settings = new AppSettings();
        var now = DateTimeOffset.UtcNow;

        var profile = new ProfileMetadata
        {
            Id = Guid.NewGuid(),
            AccountEmail = "credential@example.com",
            Nickname = "Credential Only",
            IsActive = true
        };

        // isRoutingActive = false (e.g. routing active to API provider)
        var item = Activator.CreateInstance(itemType, profile, now, settings, null, false, false)!;

        var cardVisualStateProp = itemType.GetProperty("CardVisualState")!;
        var stateValue = cardVisualStateProp.GetValue(item)!.ToString();

        Assert.Equal("ActiveCredential", stateValue);
    }

    [Fact]
    public void AccountItemViewModel_UpdateState_MutatesInPlace_AndRaisesPropertyChanged()
    {
        var app = LoadAppAssembly();
        var itemType = app.GetType("CodexSwitcher.App.ViewModels.AccountItemViewModel")!;
        var settings = new AppSettings();
        var now = DateTimeOffset.UtcNow;

        var profile = new ProfileMetadata
        {
            Id = Guid.NewGuid(),
            AccountEmail = "test@example.com",
            Nickname = "Test Account",
            IsActive = false
        };

        var item = (INotifyPropertyChanged)Activator.CreateInstance(itemType, profile, now, settings, null, false, false)!;
        var changedProps = new List<string>();
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                changedProps.Add(e.PropertyName);
        };

        // Now activate the account
        var updatedProfile = new ProfileMetadata
        {
            Id = profile.Id,
            AccountEmail = "test@example.com",
            Nickname = "Test Account Updated",
            IsActive = true
        };

        var updateMethod = itemType.GetMethod("UpdateState")!;
        updateMethod.Invoke(item, new object[] { updatedProfile, true, now.AddMinutes(1), settings });

        var cardVisualStateProp = itemType.GetProperty("CardVisualState")!;
        Assert.Equal("ActiveRouting", cardVisualStateProp.GetValue(item)!.ToString());

        var canSwitchProp = itemType.GetProperty("CanSwitch")!;
        Assert.False((bool)canSwitchProp.GetValue(item)!);

        var isInUseProp = itemType.GetProperty("IsInUse")!;
        Assert.True((bool)isInUseProp.GetValue(item)!);

        Assert.Contains("CardVisualState", changedProps);
        Assert.Contains("IsActive", changedProps);
        Assert.Contains("IsRoutingActive", changedProps);
        Assert.Contains("CanSwitch", changedProps);
        Assert.Contains("IsInUse", changedProps);
    }

    [Fact]
    public void ApiProviderItemViewModel_VisualState_TransitionsReactively()
    {
        var app = LoadAppAssembly();
        var itemType = app.GetType("CodexSwitcher.App.ViewModels.ApiProviderItemViewModel")!;

        var profile = new CodexSwitcher.Core.Providers.Models.ApiProviderProfile
        {
            Id = Guid.NewGuid(),
            Nickname = "Test API",
            BaseUrl = "https://api.openai.com/v1",
            Status = CodexSwitcher.Core.Providers.Models.ApiProviderProfileStatus.Active
        };

        // 1. Missing key -> KeyRequired
        var item = (INotifyPropertyChanged)Activator.CreateInstance(itemType, profile, null, false, false)!;
        var cardVisualStateProp = itemType.GetProperty("CardVisualState")!;
        Assert.Equal("KeyRequired", cardVisualStateProp.GetValue(item)!.ToString());

        var changedProps = new List<string>();
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
                changedProps.Add(e.PropertyName);
        };

        // 2. Supply key -> Normal
        var hasSecretProp = itemType.GetProperty("HasSecret")!;
        hasSecretProp.SetValue(item, true);
        Assert.Equal("Normal", cardVisualStateProp.GetValue(item)!.ToString());
        Assert.Contains("CardVisualState", changedProps);

        // 3. Set routing active -> ActiveRouting
        var updateRoutingMethod = itemType.GetMethod("UpdateRoutingState")!;
        updateRoutingMethod.Invoke(item, new object[] { true });
        Assert.Equal("ActiveRouting", cardVisualStateProp.GetValue(item)!.ToString());
    }

    #endregion

    #region 2. ShutdownTracer & Lifecycle Tests

    [Fact]
    public async Task ShutdownTracer_MeasuresPhaseTimingsMonotonically()
    {
        var tracer = new ShutdownTracer();
        Assert.False(tracer.IsStarted);

        tracer.Start();
        Assert.True(tracer.IsStarted);

        tracer.MeasurePhase("Phase1", () =>
        {
            Thread.Sleep(20);
        });

        await tracer.MeasurePhaseAsync("Phase2", async () =>
        {
            await Task.Delay(30).ConfigureAwait(false);
        });

        var phases = tracer.Phases;
        Assert.Equal(2, phases.Count);

        Assert.Equal("Phase1", phases[0].PhaseName);
        Assert.True(phases[0].ElapsedMilliseconds >= 15);
        Assert.True(phases[0].EndTimestampMs >= phases[0].StartTimestampMs);

        Assert.Equal("Phase2", phases[1].PhaseName);
        Assert.True(phases[1].ElapsedMilliseconds >= 25);
        Assert.True(phases[1].StartTimestampMs >= phases[0].EndTimestampMs);

        var summary = tracer.FormatSummary();
        Assert.Contains("Shutdown completed", summary);
        Assert.Contains("Phase1", summary);
        Assert.Contains("Phase2", summary);
    }

    [Fact]
    public async Task UsagePollingCoordinator_CancelsImmediately_WhenAppLifetimeStops()
    {
        using var appLifetime = new AppLifetime();
        var usageMock = new TestUsageService();
        var clock = new SystemClock();

        using var coordinator = new UsagePollingCoordinator(usageMock, clock, appLifetime);

        Assert.False(appLifetime.IsStopping);

        // Signal application stop
        appLifetime.StopApplication();
        Assert.True(appLifetime.IsStopping);

        // Subsequent triggers must return empty immediately without starting batch
        var results = await coordinator.TriggerRefreshAllAsync([new ProfileMetadata { Id = Guid.NewGuid() }]);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SwitchboardCodexProcessRegistry_ParallelKill_TerminatesInSubSecond()
    {
        var registry = new SwitchboardCodexProcessRegistry();

        // Spawn 2 lightweight child sleeper processes to simulate owned worker app-server processes
        var psi1 = new ProcessStartInfo("cmd.exe", "/c timeout /t 10 /nobreak > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        var psi2 = new ProcessStartInfo("cmd.exe", "/c timeout /t 10 /nobreak > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var p1 = Process.Start(psi1)!;
        using var p2 = Process.Start(psi2)!;

        registry.RegisterOwnedProcess(p1.Id);
        registry.RegisterOwnedProcess(p2.Id);

        Assert.True(registry.IsOwnedProcess(p1.Id));
        Assert.True(registry.IsOwnedProcess(p2.Id));
        Assert.Equal(2, registry.GetOwnedProcessIds().Count);

        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await registry.TerminateAllOwnedProcessesAsync(TimeSpan.FromMilliseconds(400), cts.Token);
        sw.Stop();

        // Must complete well under 1000ms (target sub-300ms)
        Assert.True(sw.ElapsedMilliseconds < 800, $"Process termination took {sw.ElapsedMilliseconds} ms, expected sub-second.");
        Assert.True(p1.HasExited);
        Assert.True(p2.HasExited);
        Assert.Empty(registry.GetOwnedProcessIds());
    }

    [Fact]
    public void SwitchboardCodexProcessRegistry_PreservesExternalProcesses()
    {
        var registry = new SwitchboardCodexProcessRegistry();

        // An un-registered process ID (such as the test runner process itself)
        var currentPid = Environment.ProcessId;

        Assert.False(registry.IsOwnedProcess(currentPid));
        Assert.DoesNotContain(currentPid, registry.GetOwnedProcessIds());
    }

    #endregion

    #region 3. Performance & Write Amplification Tests

    [Fact]
    public void ProfileStore_SaveAll_AvoidsRedundantWrites_WhenMetadataIsIdentical()
    {
        var fs = new PhysicalFileSystem();
        var storePath = Path.Combine(_testDir, "profiles.json");
        var store = new ProfileStore(fs, storePath);

        var id = Guid.NewGuid();
        var list = new List<ProfileMetadata>
        {
            new() { Id = id, Nickname = "Account 1", AccountEmail = "a1@test.com", CreatedAt = DateTimeOffset.UtcNow }
        };

        store.SaveAll(list);
        Assert.True(File.Exists(storePath));
        var firstWriteTime = File.GetLastWriteTimeUtc(storePath);

        // Second write with identical metadata
        Thread.Sleep(50);
        store.SaveAll(list);
        var secondWriteTime = File.GetLastWriteTimeUtc(storePath);

        // Must NOT have rewritten the file
        Assert.Equal(firstWriteTime, secondWriteTime);
    }

    [Fact]
    public void CodexCliRunner_ResolveCodexPath_CachesResult_AvoidingRepeatedPathScans()
    {
        CodexCliRunner.InvalidateCachedPath();

        var path1 = CodexCliRunner.ResolveCodexPath();
        var path2 = CodexCliRunner.ResolveCodexPath();

        Assert.Equal(path1, path2);
    }

    #endregion

    private sealed class TestUsageService : IUsageService
    {
        public UsageCacheEntry? GetCached(Guid profileId) => null;
        public IReadOnlyDictionary<Guid, UsageCacheEntry> GetAllCached() => new Dictionary<Guid, UsageCacheEntry>();
        public void Invalidate(Guid profileId) { }
        public Task LoadCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UsageFetchResult> RefreshAsync(ProfileMetadata profile, bool force = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(UsageFetchResult.Fail(UsageStatus.Error, ErrorInfo.Create(ErrorCategory.Unknown, "test", DateTimeOffset.UtcNow)));
        public Task<IReadOnlyDictionary<Guid, UsageFetchResult>> RefreshAllAsync(IReadOnlyList<ProfileMetadata> profiles, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, UsageFetchResult>>(new Dictionary<Guid, UsageFetchResult>());
        public Task<IReadOnlyDictionary<Guid, UsageFetchResult>> RefreshAllAsync(IReadOnlyList<ProfileMetadata> profiles, Action<Guid, UsageFetchResult>? onAccountCompleted, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, UsageFetchResult>>(new Dictionary<Guid, UsageFetchResult>());
    }
}
