using Fabrica.Core.Jobs;
using Fabrica.Core.Threading;
using Xunit;

namespace Fabrica.Core.Tests.Threading;

public sealed class ThreadPinningTests
{
    [Fact]
    public void TryPinCurrentThread_CoreZero_SucceedsOnSupportedPlatform()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.True(ThreadPinningNative.TryPinCurrentThread(0));
            return;
        }

        Assert.False(ThreadPinningNative.TryPinCurrentThread(0));
    }

    [Fact]
    public void TryPinCurrentThread_OutOfRange_ReturnsFalse()
    {
        Assert.False(ThreadPinningNative.TryPinCurrentThread(-1));
        Assert.False(ThreadPinningNative.TryPinCurrentThread(Environment.ProcessorCount));
        Assert.False(ThreadPinningNative.TryPinCurrentThread(int.MaxValue));
    }

    [Fact]
    public void TryPinCurrentThread_Linux_AffinityMaskReflectsPin()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var core = Math.Min(1, Environment.ProcessorCount - 1);
        Assert.True(ThreadPinningNative.TryPinCurrentThread(core));
        Assert.True(ThreadPinningNative.TryGetCurrentThreadAffinity(out var mask));
        Assert.Equal(1UL << core, mask);
    }

    [Fact]
    public void TryPinCurrentThread_Linux_EachCoreGetsSeparateMask()
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (Environment.ProcessorCount < 2)
            return;

        Assert.True(ThreadPinningNative.TryPinCurrentThread(0));
        Assert.True(ThreadPinningNative.TryGetCurrentThreadAffinity(out var mask0));
        Assert.Equal(1UL << 0, mask0);

        Assert.True(ThreadPinningNative.TryPinCurrentThread(1));
        Assert.True(ThreadPinningNative.TryGetCurrentThreadAffinity(out var mask1));
        Assert.Equal(1UL << 1, mask1);

        Assert.NotEqual(mask0, mask1);
    }

    /// <summary>
    /// Verifies that <see cref="ThreadPinningNative.StartNativeThreadWithHighQos"/> creates a thread
    /// with <c>QOS_CLASS_USER_INITIATED</c> on macOS. This exercises the <c>pthread_attr_set_qos_class_np</c>
    /// path that bypasses the .NET thread limitation.
    /// </summary>
    [Fact]
    public void StartNativeThreadWithHighQos_MacOS_SetsUserInitiatedQos()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        uint observedQos = 0;
        var done = new ManualResetEventSlim(false);

        ThreadPinningNative.StartNativeThreadWithHighQos(
            "QoS-Test",
            () =>
            {
                ThreadPinningNative.TryGetCurrentThreadQos(out observedQos);
                done.Set();
                Thread.Sleep(10);
            },
            coreIndex: 0);

        Assert.True(done.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(ThreadPinningNative.QOS_CLASS_USER_INITIATED, observedQos);
    }

    /// <summary>
    /// Verifies that <see cref="ThreadPinningNative.StartNativeThreadWithHighQos"/> creates threads with
    /// proper pinning on Linux.
    /// </summary>
    [Fact]
    public void StartNativeThreadWithHighQos_Linux_PinsToCore()
    {
        if (!OperatingSystem.IsLinux())
            return;

        ulong observedMask = 0;
        var done = new ManualResetEventSlim(false);

        ThreadPinningNative.StartNativeThreadWithHighQos(
            "Pin-Test",
            () =>
            {
                ThreadPinningNative.TryGetCurrentThreadAffinity(out observedMask);
                done.Set();
                Thread.Sleep(10);
            },
            coreIndex: 0);

        Assert.True(done.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1UL << 0, observedMask);
    }

    /// <summary>
    /// End-to-end: creates a <see cref="WorkerPool"/> and verifies that background worker threads
    /// actually have <c>QOS_CLASS_USER_INITIATED</c> on macOS. Uses 3 blocking jobs to force all
    /// threads (2 workers + 1 coordinator) to participate simultaneously, guaranteeing that at
    /// least 2 jobs execute on background worker threads.
    /// </summary>
    [Fact]
    public void WorkerPool_BackgroundWorkers_HaveHighQosOnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        const int WorkerCount = 2;
        const int ThreadCount = 3;
        using var pool = new WorkerPool(workerCount: WorkerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);

        var results = new QosResult[ThreadCount];
        var rendezvous = new Barrier(ThreadCount);
        var done = new CountdownEvent(ThreadCount);

        for (var i = 0; i < ThreadCount; i++)
        {
            results[i] = new QosResult();
            var job = new RendezvousQosJob { Result = results[i], Rendezvous = rendezvous, Done = done };
            scheduler.GetTestAccessor().Inject(job);
        }

        scheduler.GetTestAccessor().WaitForCompletion();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var bgCount = 0;
        foreach (var r in results)
        {
            if (r.WorkerIndex < WorkerCount)
            {
                bgCount++;
                Assert.True(
                    r.Qos == ThreadPinningNative.QOS_CLASS_USER_INITIATED,
                    $"Worker {r.WorkerIndex} QoS: expected 0x{ThreadPinningNative.QOS_CLASS_USER_INITIATED:X}, got 0x{r.Qos:X}");
            }
        }

        Assert.Equal(WorkerCount, bgCount);
    }

    /// <summary>
    /// End-to-end: creates a <see cref="WorkerPool"/> and verifies that background worker threads
    /// are pinned to their expected cores on Linux. Uses blocking rendezvous to force all threads
    /// to participate.
    /// </summary>
    [Fact]
    public void WorkerPool_BackgroundWorkers_ArePinnedOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (Environment.ProcessorCount < 2)
            return;

        const int WorkerCount = 2;
        const int ThreadCount = 3;
        using var pool = new WorkerPool(workerCount: WorkerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);

        var results = new AffinityResult[ThreadCount];
        var rendezvous = new Barrier(ThreadCount);
        var done = new CountdownEvent(ThreadCount);

        for (var i = 0; i < ThreadCount; i++)
        {
            results[i] = new AffinityResult();
            var job = new RendezvousAffinityJob { Result = results[i], Rendezvous = rendezvous, Done = done };
            scheduler.GetTestAccessor().Inject(job);
        }

        scheduler.GetTestAccessor().WaitForCompletion();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var bgCount = 0;
        foreach (var r in results)
        {
            if (r.WorkerIndex < WorkerCount)
            {
                bgCount++;
                Assert.True(r.Mask != 0, $"Worker {r.WorkerIndex} should have a non-zero affinity mask");
                Assert.True(IsSingleBit(r.Mask), $"Worker {r.WorkerIndex} should be pinned to exactly one core");
            }
        }

        Assert.Equal(WorkerCount, bgCount);
    }

    private static bool IsSingleBit(ulong v) => v != 0 && (v & (v - 1)) == 0;

    private sealed class QosResult
    {
        public uint Qos;
        public int WorkerIndex = -1;
    }

    /// <summary>
    /// Job that blocks at a <see cref="Barrier"/> rendezvous, forcing all threads (workers +
    /// coordinator) to participate simultaneously before any completes.
    /// </summary>
    private sealed class RendezvousQosJob : Job
    {
        internal QosResult Result = null!;
        internal Barrier Rendezvous = null!;
        internal CountdownEvent Done = null!;

        protected internal override void Execute(JobContext context)
        {
            ThreadPinningNative.TryGetCurrentThreadQos(out var qos);
            Result.Qos = qos;
            Result.WorkerIndex = context.WorkerIndex;
            Rendezvous.SignalAndWait(TimeSpan.FromSeconds(5));
            Done.Signal();
        }

        protected override void ResetState() { }
    }

    private sealed class AffinityResult
    {
        public ulong Mask;
        public int WorkerIndex = -1;
    }

    private sealed class RendezvousAffinityJob : Job
    {
        internal AffinityResult Result = null!;
        internal Barrier Rendezvous = null!;
        internal CountdownEvent Done = null!;

        protected internal override void Execute(JobContext context)
        {
            ThreadPinningNative.TryGetCurrentThreadAffinity(out var mask);
            Result.Mask = mask;
            Result.WorkerIndex = context.WorkerIndex;
            Rendezvous.SignalAndWait(TimeSpan.FromSeconds(5));
            Done.Signal();
        }

        protected override void ResetState() { }
    }
}
