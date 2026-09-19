using Xunit;

namespace CodexSwitcher.Core.Tests;

public class AsyncPriorityThrottleTests
{
    [Fact]
    public void Constructor_ValidatesPositiveConcurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AsyncPriorityThrottle(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AsyncPriorityThrottle(-1));
        using var throttle = new AsyncPriorityThrottle(2);
        Assert.Equal(2, throttle.MaxConcurrency);
        Assert.Equal(0, throttle.ActiveCount);
    }

    [Fact]
    public async Task AcquireAsync_BouncesBeyondMaxConcurrency()
    {
        using var throttle = new AsyncPriorityThrottle(2);

        var r1 = await throttle.AcquireAsync(UsagePriority.Background);
        var r2 = await throttle.AcquireAsync(UsagePriority.Background);
        Assert.Equal(2, throttle.ActiveCount);

        // Third acquisition should not complete immediately
        var acquireTask = throttle.AcquireAsync(UsagePriority.Background).AsTask();
        Assert.False(acquireTask.IsCompleted);
        Assert.Equal(1, throttle.WaitingLowPriorityCount);

        // Releasing one allows third to acquire
        r1.Dispose();
        var r3 = await acquireTask;
        Assert.Equal(2, throttle.ActiveCount);
        Assert.Equal(0, throttle.WaitingLowPriorityCount);

        r2.Dispose();
        r3.Dispose();
        Assert.Equal(0, throttle.ActiveCount);
    }

    [Fact]
    public async Task AcquireAsync_InteractivePreemptsBackgroundWaiters()
    {
        using var throttle = new AsyncPriorityThrottle(1);

        var r1 = await throttle.AcquireAsync(UsagePriority.Background);

        // Enqueue background waiters first
        var bgTask1 = throttle.AcquireAsync(UsagePriority.Background).AsTask();
        var bgTask2 = throttle.AcquireAsync(UsagePriority.Background).AsTask();

        // Enqueue interactive waiter after
        var interactiveTask = throttle.AcquireAsync(UsagePriority.Interactive).AsTask();

        Assert.Equal(2, throttle.WaitingLowPriorityCount);
        Assert.Equal(1, throttle.WaitingHighPriorityCount);

        // Release first slot -> interactive MUST be completed before either background
        r1.Dispose();

        var completedFirst = await Task.WhenAny(interactiveTask, bgTask1, bgTask2);
        Assert.Same(interactiveTask, completedFirst);
        Assert.True(interactiveTask.IsCompletedSuccessfully);
        Assert.False(bgTask1.IsCompleted);
        Assert.False(bgTask2.IsCompleted);

        // Clean up
        var interactiveReleaser = await interactiveTask;
        interactiveReleaser.Dispose();

        var completedSecond = await Task.WhenAny(bgTask1, bgTask2);
        Assert.Same(bgTask1, completedSecond);
        var bg1 = await bgTask1;
        bg1.Dispose();

        var bg2 = await bgTask2;
        bg2.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_CancellationRemovesWaiterCleanly()
    {
        using var throttle = new AsyncPriorityThrottle(1);
        var r1 = await throttle.AcquireAsync(UsagePriority.Background);

        using var cts = new CancellationTokenSource();
        var waitTask = throttle.AcquireAsync(UsagePriority.Interactive, cts.Token).AsTask();

        Assert.Equal(1, throttle.WaitingHighPriorityCount);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
        Assert.Equal(0, throttle.WaitingHighPriorityCount);

        r1.Dispose();
        Assert.Equal(0, throttle.ActiveCount);
    }

    [Fact]
    public async Task Dispose_CancelsPendingWaiters()
    {
        var throttle = new AsyncPriorityThrottle(1);
        var r1 = await throttle.AcquireAsync(UsagePriority.Background);

        var bgTask = throttle.AcquireAsync(UsagePriority.Background).AsTask();
        var highTask = throttle.AcquireAsync(UsagePriority.Interactive).AsTask();

        throttle.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await bgTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await highTask);

        r1.Dispose();
    }
}
