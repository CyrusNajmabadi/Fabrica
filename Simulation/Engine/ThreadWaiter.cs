namespace Simulation.Engine;

/// <summary>
/// Production waiter backed by <see cref="WaitHandle.WaitOne(TimeSpan)"/> so waits
/// remain cancellation-aware without hard-coding <see cref="Thread.Sleep"/>.
/// </summary>
internal readonly struct ThreadWaiter : IWaiter
{
    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
            return;

        int signaled = WaitHandle.WaitAny(
            [cancellationToken.WaitHandle],
            duration);

        if (signaled == 0 && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
    }
}
