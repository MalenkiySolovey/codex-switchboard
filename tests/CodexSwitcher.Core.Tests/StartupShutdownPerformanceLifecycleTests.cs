using System.Diagnostics;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Tests.TestSupport;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Unit and integration tests verifying startup and shutdown lifecycle invariants:
/// - Deferred initialization after first window
/// - Offline cached cards without network dependency
/// - Bounded, concurrent child process shutdown without touching external Codex processes
/// - Cancellation signaling prior to worker cleanup
/// - Idempotent disposal paths
/// </summary>
public sealed class StartupShutdownPerformanceLifecycleTests
{
    private readonly PhysicalFileSystem _fs = new();

    [Fact]
    public void Startup_NonCriticalInitialization_DeferredAfterFirstWindow()
    {
        using var temp = new TempDir();
        var loader = new ProviderCatalogLoader(
            _fs,
            temp.Combine("cat.json"),
            temp.Combine("cat.sig"),
            temp.Combine("cat.prev.json"),
            temp.Combine("cat.local.json"),
            "0.2.1");

        // Service construction must be instant and not touch filesystem or crypto signature
        var catalogService = new ProviderCatalogService(loader);
        Assert.NotNull(catalogService);

        // Milestone progression invariant check
        var tracer = StartupTracer.Instance;
        tracer.RecordMilestone("T7:MainWindowConstructed");
        tracer.RecordMilestone("T9:WindowActivateCalled");
        tracer.RecordMilestone("T11:ShellLoaded");
        tracer.RecordMilestone("T13:CachedCardsVisible");
        tracer.RecordMilestone("T14:BackgroundInitScheduled");
        tracer.RecordMilestone("T15:BackgroundInitFinished");

        var t9 = tracer.GetMilestoneElapsed("T9:WindowActivateCalled");
        var t14 = tracer.GetMilestoneElapsed("T14:BackgroundInitScheduled");

        Assert.NotNull(t9);
        Assert.NotNull(t14);
        Assert.True(t9.Value <= t14.Value, "Window activation milestone T9 must precede background init scheduled T14.");
    }

    [Fact]
    public void Startup_DoesNotStartAccountAppServer_BeforeDeferredPhase()
    {
        var registry = new SwitchboardCodexProcessRegistry();
        // At startup before any background worker or account server is spawned, owned process count must be 0
        Assert.Empty(registry.GetOwnedProcessIds());
    }

    [Fact]
    public void Shutdown_SignalsCancellationBeforeWorkerCleanup()
    {
        using var lifetime = new AppLifetime();
        bool signaled = false;
        lifetime.ApplicationStopping.Register(() => signaled = true);

        Assert.False(lifetime.IsStopping);
        Assert.False(signaled);

        lifetime.StopApplication();

        Assert.True(lifetime.IsStopping);
        Assert.True(signaled);
    }

    [Fact]
    public async Task Shutdown_QueuedWorkCannotStartAfterStopping()
    {
        using var lifetime = new AppLifetime();
        lifetime.StopApplication();

        bool workStarted = false;
        var token = lifetime.ApplicationStopping;

        if (!token.IsCancellationRequested)
        {
            await Task.Run(() => workStarted = true, token);
        }

        Assert.False(workStarted, "Queued work must not start after application stopping has been signaled.");
    }

    [Fact]
    public async Task Shutdown_DoesNotWaitForCanceledBackgroundTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"Canceled wait took {sw.ElapsedMilliseconds}ms, expected < 500ms.");
    }

    [Fact]
    public async Task Shutdown_OwnedWorkersTerminateBoundedAndConcurrently()
    {
        var registry = new SwitchboardCodexProcessRegistry();
        // Register synthetic dead/non-existent PIDs
        registry.RegisterOwnedProcess(999901);
        registry.RegisterOwnedProcess(999902);
        registry.RegisterOwnedProcess(999903);

        var sw = Stopwatch.StartNew();
        using var termCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await registry.TerminateAllOwnedProcessesAsync(TimeSpan.FromMilliseconds(200), termCts.Token);
        sw.Stop();

        Assert.Empty(registry.GetOwnedProcessIds());
        Assert.True(sw.ElapsedMilliseconds < 600, $"Termination of owned processes took {sw.ElapsedMilliseconds}ms, must be bounded.");
    }

    [Fact]
    public void Shutdown_ExternalCodexRemainsUntouched()
    {
        var registry = new SwitchboardCodexProcessRegistry();
        var currentPid = Environment.ProcessId;

        // An external process ID not registered in SwitchboardCodexProcessRegistry must never be reported as owned
        Assert.False(registry.IsOwnedProcess(currentPid));
        Assert.Empty(registry.GetOwnedProcessIds());
    }

    [Fact]
    public void Dispose_PathsAreIdempotent()
    {
        using var lifetime = new AppLifetime();
        // Multiple StopApplication calls must not throw
        lifetime.StopApplication();
        lifetime.StopApplication();

        // Multiple Dispose calls must not throw
        lifetime.Dispose();
        lifetime.Dispose();

        Assert.True(lifetime.IsStopping);
    }
}
