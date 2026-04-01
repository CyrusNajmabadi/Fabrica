namespace Fabrica.Pipeline.Threading;

/// <summary>
/// Blocking delay abstraction. Implementations are constrained to struct so the JIT/AOT can devirtualise calls — zero
/// interface-dispatch overhead in the hot loop. Injecting this abstraction makes all timing behaviour controllable in tests
/// without real sleeps. See ThreadWaiter for the production implementation.
/// </summary>
public interface IWaiter
{
    void Wait(TimeSpan duration, CancellationToken cancellationToken);
}
