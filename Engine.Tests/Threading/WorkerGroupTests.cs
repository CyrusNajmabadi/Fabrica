using Engine.Threading;
using Xunit;

namespace Engine.Tests.Threading;

public sealed class WorkerGroupTests
{
    private const int TimeoutMilliseconds = 5_000;

    [Fact]
    public void Dispatch_CompletesWhenCancelled_DoesNotDeadlock()
    {
        using var cts = new CancellationTokenSource();
        var gate = new ManualResetEventSlim(false);

        var group = new WorkerGroup<EmptyState, BlockUntilCancelledExecutor>(
            workerCount: 2,
            i => new BlockUntilCancelledExecutor(gate),
            "CancelTest");

        var dispatchReturned = false;
        var dispatchThread = new Thread(() =>
        {
            group.Dispatch(default, cts.Token);
            Volatile.Write(ref dispatchReturned, true);
        })
        { IsBackground = true };

        dispatchThread.Start();

        gate.Wait(TestContext.Current.CancellationToken);
        cts.Cancel();

        var joined = dispatchThread.Join(TimeoutMilliseconds);
        group.Shutdown();

        Assert.True(joined, "Dispatch did not return after cancellation — deadlock detected.");
        Assert.True(Volatile.Read(ref dispatchReturned));
    }

    [Fact]
    public void Dispatch_CompletesWhenExecutorThrows_DoesNotDeadlock()
    {
        var group = new WorkerGroup<EmptyState, ThrowingExecutor>(
            workerCount: 2,
            _ => new ThrowingExecutor(),
            "ThrowTest");

        using var cts = new CancellationTokenSource();

        var dispatchReturned = false;
        var dispatchThread = new Thread(() =>
        {
            group.Dispatch(default, cts.Token);
            Volatile.Write(ref dispatchReturned, true);
        })
        { IsBackground = true };

        dispatchThread.Start();

        var joined = dispatchThread.Join(TimeoutMilliseconds);
        group.Shutdown();

        Assert.True(joined, "Dispatch did not return after executor exception — deadlock detected.");
        Assert.True(Volatile.Read(ref dispatchReturned));
    }

    // ── Test executors ────────────────────────────────────────────────────

    private struct EmptyState;

    /// <summary>
    /// Blocks in Execute until the cancellation token is signalled.
    /// Sets the gate once at least one worker has entered Execute,
    /// so the test knows it's safe to cancel.
    /// </summary>
    private readonly struct BlockUntilCancelledExecutor(ManualResetEventSlim gate) : IThreadExecutor<EmptyState>
    {
        private readonly ManualResetEventSlim _gate = gate;

        public readonly void Prepare() { }

        public readonly void Execute(in EmptyState state, CancellationToken cancellationToken)
        {
            _gate.Set();
            cancellationToken.WaitHandle.WaitOne();
        }
    }

    /// <summary>
    /// Always throws from Execute.
    /// </summary>
    private readonly struct ThrowingExecutor : IThreadExecutor<EmptyState>
    {
        public void Prepare() { }

        public void Execute(in EmptyState state, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Deliberate test exception.");
    }
}
