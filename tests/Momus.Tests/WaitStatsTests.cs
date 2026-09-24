using Momus.SqlServer.Checks;

namespace Momus.Tests;

/// <summary>
/// Which SQL Server waits are a thread with nothing to do. Too few in the benign list and a quiet
/// server's scan is five findings of noise; one too many and a real bottleneck disappears. Both
/// directions are pinned.
/// </summary>
public class WaitStatsTests
{
    [Theory]
    // The top of sys.dm_os_wait_stats on two idle SQL Server 2022 instances on 2026-09-24, one
    // fresh and one after weeks of development use. SOS_WORK_DISPATCHER alone was 94% of the wait
    // time, so before these were filtered every scan led with them.
    [InlineData("SOS_WORK_DISPATCHER")]
    [InlineData("SQLTRACE_INCREMENTAL_FLUSH_SLEEP")]
    [InlineData("PWAIT_EXTENSIBILITY_CLEANUP_TASK")]
    [InlineData("QDS_ASYNC_QUEUE")]
    [InlineData("BROKER_EVENTHANDLER")]
    [InlineData("AZURE_IMDS_VERSIONS")]
    [InlineData("STARTUP_DEPENDENCY_MANAGER")]
    [InlineData("PWAIT_ALL_COMPONENTS_INITIALIZED")]
    [InlineData("CHKPT")]
    [InlineData("SLEEP_MASTERDBREADY")]
    [InlineData("SLEEP_PHYSMASTERDBREADY")]
    [InlineData("SLEEP_MASTERUPGRADED")]
    [InlineData("MEMORY_ALLOCATION_EXT")]
    public void The_background_waits_of_an_idle_2022_instance_are_benign(string wait) =>
        Assert.Contains(wait, WaitStatsCheck.BenignWaits);

    [Theory]
    // Real waits, several of them seen just below the background ones on the same two instances.
    [InlineData("PAGEIOLATCH_SH")]
    [InlineData("WRITELOG")]
    [InlineData("LCK_M_X")]
    [InlineData("LCK_M_U")]
    [InlineData("SOS_SCHEDULER_YIELD")]
    [InlineData("THREADPOOL")]
    [InlineData("RESOURCE_SEMAPHORE")]
    [InlineData("ASYNC_NETWORK_IO")]
    [InlineData("CXPACKET")]
    [InlineData("CXSYNC_PORT")]
    [InlineData("LATCH_EX")]
    [InlineData("PAGELATCH_EX")]
    [InlineData("HADR_SYNC_COMMIT")]
    public void A_wait_that_means_something_is_never_benign(string wait) =>
        Assert.DoesNotContain(wait, WaitStatsCheck.BenignWaits);

    [Fact]
    public void The_list_names_each_wait_once() =>
        Assert.Equal(WaitStatsCheck.BenignWaits.Length, WaitStatsCheck.BenignWaits.Distinct().Count());
}
