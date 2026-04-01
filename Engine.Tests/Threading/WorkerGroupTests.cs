using Engine.Threading;
using Xunit;

namespace Engine.Tests.Threading;

public sealed class WorkerGroupTests
{
    private const int TimeoutMilliseconds = 5_000;

    /// <summary>
    /// Demonstrates the deadlock: when Dispatch is called with an already-cancelled
    /// token, each worker wakes from its go signal, sees IsCancellationRequested, and
    /// must still signal done so WaitAll can return.
    /// </summary>
    [Fact]
    public void Dispatch_WithAlreadyCancelledToken_DoesNotDeadlock()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var group = new WorkerGroup<EmptyState, NoOpExecutor>(
            workerCount: 2,
            _ => new NoOpExecutor(),
            "CancelTest");

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

        Assert.True(joined, "Dispatch did not return with a pre-cancelled token — deadlock detected.");
        Assert.True(Volatile.Read(ref dispatchReturned));
    }

    [Fact]
    public void Dispatch_CompletesWhenExecutorThrows_DoesNotDeadlock()
    {
        using var cts = new CancellationTokenSource();

        var group = new WorkerGroup<EmptyState, ThrowingExecutor>(
            workerCount: 2,
            _ => new ThrowingExecutor(),
            "ThrowTest");

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

    private readonly struct NoOpExecutor : IThreadExecutor<EmptyState>
    {
        public void Prepare() { }
        public void Execute(in EmptyState state, CancellationToken cancellationToken) { }
    }

    private readonly struct ThrowingExecutor : IThreadExecutor<EmptyState>
    {
        public void Prepare() { }

        public void Execute(in EmptyState state, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Deliberate test exception.");
    }
}
