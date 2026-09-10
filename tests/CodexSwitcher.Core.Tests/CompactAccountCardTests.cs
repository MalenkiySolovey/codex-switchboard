using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Support;

namespace CodexSwitcher.Core.Tests;

public sealed class CompactAccountCardTests
{
    private sealed class InMemoryFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => _files.ContainsKey(path);
        public bool DirectoryExists(string path) => true;
        public void CreateDirectory(string path) { }

        public byte[] ReadAllBytes(string path) =>
            _files.TryGetValue(path, out var text) ? System.Text.Encoding.UTF8.GetBytes(text) : throw new FileNotFoundException(path);

        public string ReadAllText(string path) =>
            _files.TryGetValue(path, out var text) ? text : throw new FileNotFoundException(path);

        public void WriteAllBytesAtomic(string path, byte[] contents) =>
            _files[path] = System.Text.Encoding.UTF8.GetString(contents);

        public void WriteAllTextAtomic(string path, string contents) =>
            _files[path] = contents;

        public void Copy(string sourcePath, string destPath, bool overwrite)
        {
            if (_files.TryGetValue(sourcePath, out var c)) _files[destPath] = c;
        }

        public void Move(string sourcePath, string destPath, bool overwrite)
        {
            if (_files.TryGetValue(sourcePath, out var c))
            {
                _files[destPath] = c;
                _files.Remove(sourcePath);
            }
        }

        public void Delete(string path) => _files.Remove(path);

        public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern) =>
            _files.Keys.ToList();
    }

    #region Settings Persistence & Backward Compatibility

    [Fact]
    public void AppSettings_DefaultsCollapsedProfileIdsToEmpty()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.CollapsedProfileIds);
        Assert.Empty(settings.CollapsedProfileIds);
    }

    [Fact]
    public void SettingsStore_RoundTripsCollapsedProfileIds()
    {
        var fs = new InMemoryFileSystem();
        var store = new SettingsStore(fs, @"C:\data\settings.json");

        var profileA = Guid.NewGuid();
        var profileB = Guid.NewGuid();

        var settings = new AppSettings();
        settings.CollapsedProfileIds.Add(profileA);
        settings.CollapsedProfileIds.Add(profileB);

        store.Save(settings);

        var loaded = store.Load();
        Assert.Contains(profileA, loaded.CollapsedProfileIds);
        Assert.Contains(profileB, loaded.CollapsedProfileIds);
        Assert.Equal(2, loaded.CollapsedProfileIds.Count);
    }

    [Fact]
    public void SettingsStore_BackwardCompatibility_Preview4SettingsDeserializeToAllExpanded()
    {
        // Old preview.4 settings JSON without collapsedProfileIds field
        var legacyJson = """
        {
          "closeReopenMode": "Automatic",
          "alwaysConfirmSwitch": true,
          "gracefulCloseTimeoutSeconds": 5,
          "activeSlotBackupsToKeep": 10,
          "codexExecutablePathOverride": null,
          "forcedTheme": null,
          "schemaVersion": 1
        }
        """;

        var fs = new InMemoryFileSystem();
        fs.WriteAllTextAtomic(@"C:\data\settings.json", legacyJson);

        var store = new SettingsStore(fs, @"C:\data\settings.json");
        var loaded = store.Load();

        Assert.NotNull(loaded.CollapsedProfileIds);
        Assert.Empty(loaded.CollapsedProfileIds); // All cards default to expanded
    }

    [Fact]
    public void SettingsStore_MalformedJson_RecoversWithDefaultSettingsSafely()
    {
        var fs = new InMemoryFileSystem();
        fs.WriteAllTextAtomic(@"C:\data\settings.json", "{ invalid json content !!!");

        var store = new SettingsStore(fs, @"C:\data\settings.json");
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Empty(loaded.CollapsedProfileIds);
    }

    [Fact]
    public void SettingsStore_OpportunisticCleanup_RemovesDeletedProfileId()
    {
        var fs = new InMemoryFileSystem();
        var store = new SettingsStore(fs, @"C:\data\settings.json");

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var settings = new AppSettings();
        settings.CollapsedProfileIds.Add(id1);
        settings.CollapsedProfileIds.Add(id2);
        store.Save(settings);

        // Simulate opportunistic cleanup upon profile deletion
        settings.CollapsedProfileIds.Remove(id1);
        store.Save(settings);

        var reloaded = store.Load();
        Assert.DoesNotContain(id1, reloaded.CollapsedProfileIds);
        Assert.Contains(id2, reloaded.CollapsedProfileIds);
    }

    #endregion

    #region Compact Countdown Formatter

    [Fact]
    public void FormatCompactCountdown_ReturnsDash_WhenNull()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("-", UsageCountdownFormatter.FormatCompactCountdown(null, now));
    }

    [Fact]
    public void FormatCompactCountdown_ReturnsResetDue_WhenPastOrNow()
    {
        var now = DateTimeOffset.UtcNow;
        var past = now.AddMinutes(-2);

        Assert.Equal("Reset due", UsageCountdownFormatter.FormatCompactCountdown(past, now, pt: false));
        Assert.Equal("Reset pendente", UsageCountdownFormatter.FormatCompactCountdown(past, now, pt: true));
        Assert.Equal("Reset due", UsageCountdownFormatter.FormatCompactCountdown(now, now, pt: false));
    }

    [Fact]
    public void FormatCompactCountdown_FormatsDaysAndHours()
    {
        var now = DateTimeOffset.UtcNow;
        var target = now.AddDays(3).AddHours(8).AddMinutes(15);

        Assert.Equal("3d 8h", UsageCountdownFormatter.FormatCompactCountdown(target, now));
    }

    [Fact]
    public void FormatCompactCountdown_FormatsHoursAndMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var target = now.AddHours(1).AddMinutes(14).AddSeconds(30);

        Assert.Equal("1h 14m", UsageCountdownFormatter.FormatCompactCountdown(target, now));
    }

    [Fact]
    public void FormatCompactCountdown_FormatsMinutes()
    {
        var now = DateTimeOffset.UtcNow;
        var target = now.AddMinutes(25).AddSeconds(10);

        Assert.Equal("25m", UsageCountdownFormatter.FormatCompactCountdown(target, now));
    }

    [Fact]
    public void FormatCompactCountdown_FormatsSeconds()
    {
        var now = DateTimeOffset.UtcNow;
        var target = now.AddSeconds(45);

        Assert.Equal("45s", UsageCountdownFormatter.FormatCompactCountdown(target, now));
    }

    #endregion

    #region Deterministic Compact Quota Window Selection Rule

    [Fact]
    public void SelectCompactWindows_EmptyList_ReturnsEmptyAndZeroAdditional()
    {
        var selected = UsagePresentationMapper.SelectCompactWindows([], out var additional);
        Assert.Empty(selected);
        Assert.Equal(0, additional);
    }

    [Fact]
    public void SelectCompactWindows_SingleWindow_ReturnsSingleAndZeroAdditional()
    {
        var w1 = new UsageWindowModel("5h", 300, "5h", 30.0, 70.0, null, false, false);
        var selected = UsagePresentationMapper.SelectCompactWindows([w1], out var additional);

        Assert.Single(selected);
        Assert.Equal("5h", selected[0].Slot);
        Assert.Equal(0, additional);
    }

    [Fact]
    public void SelectCompactWindows_TwoWindows_ReturnsBothAndZeroAdditional()
    {
        var w1 = new UsageWindowModel("5h", 300, "5h", 30.0, 70.0, null, false, false);
        var w2 = new UsageWindowModel("7d", 10080, "7d", 50.0, 50.0, null, false, false);

        var selected = UsagePresentationMapper.SelectCompactWindows([w1, w2], out var additional);

        Assert.Equal(2, selected.Count);
        Assert.Equal(0, additional);
    }

    [Fact]
    public void SelectCompactWindows_ThreeNormalWindows_PicksShortestTwoAndReportsPlusOne()
    {
        var w1 = new UsageWindowModel("45m", 45, "45m", 10.0, 90.0, null, false, false);
        var w2 = new UsageWindowModel("5h", 300, "5h", 20.0, 80.0, null, false, false);
        var w3 = new UsageWindowModel("7d", 10080, "7d", 30.0, 70.0, null, false, false);

        var selected = UsagePresentationMapper.SelectCompactWindows([w1, w2, w3], out var additional);

        Assert.Equal(2, selected.Count);
        Assert.Equal(1, additional);
        // Shortest two selected
        Assert.Equal("45m", selected[0].Slot);
        Assert.Equal("5h", selected[1].Slot);
    }

    [Fact]
    public void SelectCompactWindows_ExhaustedWindowPrioritized_EvenIfDurationIsLonger()
    {
        var w1 = new UsageWindowModel("5h", 300, "5h", 20.0, 80.0, null, false, false);
        var w2 = new UsageWindowModel("1d", 1440, "1d", 30.0, 70.0, null, false, false);
        // w3 is 7d but exhausted (limit reached!)
        var w3 = new UsageWindowModel("7d", 10080, "7d", 100.0, 0.0, null, IsExhausted: true, IsLowQuota: false);

        var selected = UsagePresentationMapper.SelectCompactWindows([w1, w2, w3], out var additional);

        Assert.Equal(2, selected.Count);
        Assert.Equal(1, additional);
        // Exhausted window w3 MUST be selected!
        Assert.Contains(selected, w => w.Slot == "7d" && w.IsExhausted);
        // The other chosen window should be the shorter healthy window w1 (5h)
        Assert.Contains(selected, w => w.Slot == "5h");
        // Result sorted by duration ascending: 5h before 7d
        Assert.Equal("5h", selected[0].Slot);
        Assert.Equal("7d", selected[1].Slot);
    }

    [Fact]
    public void SelectCompactWindows_LowQuotaWindowPrioritized_OverNormalWindows()
    {
        var w1 = new UsageWindowModel("5h", 300, "5h", 10.0, 90.0, null, false, false);
        // w2 is 1d and has low quota (< 20%)
        var w2 = new UsageWindowModel("1d", 1440, "1d", 85.0, 15.0, null, IsExhausted: false, IsLowQuota: true);
        var w3 = new UsageWindowModel("7d", 10080, "7d", 20.0, 80.0, null, false, false);

        var selected = UsagePresentationMapper.SelectCompactWindows([w1, w2, w3], out var additional);

        Assert.Equal(2, selected.Count);
        Assert.Equal(1, additional);
        // Low-quota window w2 MUST be selected
        Assert.Contains(selected, w => w.Slot == "1d" && w.IsLowQuota);
        // Shorter duration w1 (5h) is selected over w3 (7d)
        Assert.Contains(selected, w => w.Slot == "5h");
        Assert.Equal("5h", selected[0].Slot);
        Assert.Equal("1d", selected[1].Slot);
    }

    [Fact]
    public void SelectCompactWindows_ExhaustedAndLowQuota_TakeBothOverNormalWindow()
    {
        var w1 = new UsageWindowModel("5h", 300, "5h", 10.0, 90.0, null, false, false);
        var w2 = new UsageWindowModel("1d", 1440, "1d", 90.0, 10.0, null, IsExhausted: false, IsLowQuota: true);
        var w3 = new UsageWindowModel("7d", 10080, "7d", 100.0, 0.0, null, IsExhausted: true, IsLowQuota: false);

        var selected = UsagePresentationMapper.SelectCompactWindows([w1, w2, w3], out var additional);

        Assert.Equal(2, selected.Count);
        Assert.Equal(1, additional);
        // Both critical windows (w3 exhausted, w2 low quota) selected, w1 excluded
        Assert.Contains(selected, w => w.Slot == "1d");
        Assert.Contains(selected, w => w.Slot == "7d");
        Assert.DoesNotContain(selected, w => w.Slot == "5h");
    }

    [Fact]
    public void SelectCompactWindows_ArbitraryDurationsSupported()
    {
        // Non-standard window durations like 45m, 120m, 600m, 14400m
        var w1 = new UsageWindowModel("slot-45", 45, "45m", 0, 100, null, false, false);
        var w2 = new UsageWindowModel("slot-120", 120, "2h", 0, 100, null, false, false);
        var w3 = new UsageWindowModel("slot-600", 600, "10h", 0, 100, null, false, false);
        var w4 = new UsageWindowModel("slot-14400", 14400, "10d", 0, 100, null, false, false);

        var selected = UsagePresentationMapper.SelectCompactWindows([w1, w2, w3, w4], out var additional);

        Assert.Equal(2, selected.Count);
        Assert.Equal(2, additional);
        Assert.Equal("45m", selected[0].DisplayLabel);
        Assert.Equal("2h", selected[1].DisplayLabel);
    }

    #endregion

    #region Truthful State Presentation

    [Fact]
    public void NeverLoadedState_DoesNotInventFakeQuota()
    {
        var profileId = Guid.NewGuid();
        var state = AccountUsageState.NeverLoadedState(profileId);

        Assert.Equal(UsageVisualState.NeverLoaded, state.VisualState);
        Assert.Empty(state.Windows);
        Assert.Null(state.PlanType);
        Assert.Null(state.ResetCreditsAvailable);
        Assert.False(state.IsStale);
        Assert.False(state.IsRefreshing);
    }

    [Fact]
    public void StaleState_PreservesTruthfulVisualState()
    {
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var entry = new UsageCacheEntry(
            ProfileId: profileId,
            ObservedAt: now.AddHours(-3),
            Snapshot: null,
            Status: UsageStatus.Healthy,
            LastError: null,
            IsStale: true);

        var state = UsagePresentationMapper.MapFromCache(entry, profileId, now);

        Assert.True(state.IsStale);
        Assert.Equal(UsageVisualState.StaleCache, state.VisualState);
    }

    [Fact]
    public void RateLimitedState_PreservesTruthfulVisualState()
    {
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var entry = new UsageCacheEntry(
            ProfileId: profileId,
            ObservedAt: now,
            Snapshot: new RateLimitsSnapshot(
                ProfileId: profileId,
                ObservedAt: now,
                PrimaryLimitId: "codex",
                Limits: [new LimitBucket(
                    LimitId: "codex",
                    LimitName: "codex",
                    Windows: [new UsageWindow("5h", 300, "5h", 100.0, 0.0, now.AddHours(2))],
                    RateLimitReachedType: "primary_window_exhausted",
                    PlanType: "plus")],
                ResetCreditsAvailable: 0,
                PlanType: "plus",
                AccountEmail: null,
                Status: UsageStatus.RateLimited),
            Status: UsageStatus.RateLimited,
            LastError: null,
            IsStale: false);

        var state = UsagePresentationMapper.MapFromCache(entry, profileId, now);

        Assert.Equal(UsageVisualState.RateLimited, state.VisualState);
        Assert.Single(state.Windows);
        Assert.True(state.Windows[0].IsExhausted);
    }

    #endregion
}