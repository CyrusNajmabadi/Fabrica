namespace Simulation.Engine;

/// <summary>
/// Wait abstraction used to make timing behavior controllable in tests without
/// paying interface-dispatch costs in production loops.
/// </summary>
internal interface IWaiter
{
    void Wait(TimeSpan duration, CancellationToken cancellationToken);
}
