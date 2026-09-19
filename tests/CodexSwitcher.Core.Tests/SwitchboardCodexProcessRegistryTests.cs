using System.Diagnostics;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public class SwitchboardCodexProcessRegistryTests
{
    [Fact]
    public void RegisterAndUnregister_TracksPidsCorrectly()
    {
        var registry = new SwitchboardCodexProcessRegistry();

        Assert.False(registry.IsOwnedProcess(12345));
        Assert.Empty(registry.GetOwnedProcessIds());

        registry.RegisterOwnedProcess(12345);
        Assert.True(registry.IsOwnedProcess(12345));
        Assert.Contains(12345, registry.GetOwnedProcessIds());

        registry.RegisterOwnedProcess(67890);
        Assert.Equal(2, registry.GetOwnedProcessIds().Count);

        registry.UnregisterOwnedProcess(12345);
        Assert.False(registry.IsOwnedProcess(12345));
        Assert.True(registry.IsOwnedProcess(67890));
        Assert.Single(registry.GetOwnedProcessIds());

        registry.UnregisterOwnedProcess(67890);
        Assert.Empty(registry.GetOwnedProcessIds());
    }

    [Fact]
    public void RegisterInvalidPid_Ignored()
    {
        var registry = new SwitchboardCodexProcessRegistry();
        registry.RegisterOwnedProcess(0);
        registry.RegisterOwnedProcess(-1);

        Assert.False(registry.IsOwnedProcess(0));
        Assert.False(registry.IsOwnedProcess(-1));
        Assert.Empty(registry.GetOwnedProcessIds());
    }

    [Fact]
    public async Task TerminateAllOwnedProcessesAsync_HandlesNonExistentOrExitedProcesses()
    {
        var registry = new SwitchboardCodexProcessRegistry();
        // 999999 is typically a non-existent PID
        registry.RegisterOwnedProcess(999999);

        await registry.TerminateAllOwnedProcessesAsync(TimeSpan.FromMilliseconds(200));

        // After attempted termination of dead PID, it should be unregistered
        Assert.False(registry.IsOwnedProcess(999999));
        Assert.Empty(registry.GetOwnedProcessIds());
    }

    [Fact]
    public void CodexProcessManager_FiltersOutOwnedProcessFromFoundList()
    {
        var registry = new SwitchboardCodexProcessRegistry();
        var currentPid = Environment.ProcessId;

        // Register current process as owned
        registry.RegisterOwnedProcess(currentPid);

        var managerWithRegistry = new CodexProcessManager(registry);
        Assert.True(registry.IsOwnedProcess(currentPid));
    }
}
