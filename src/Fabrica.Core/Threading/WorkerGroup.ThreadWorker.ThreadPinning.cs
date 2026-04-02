namespace Fabrica.Core.Threading;

public sealed partial class WorkerGroup<TState, TExecutor>
{
    /// <summary>
    /// Best-effort thread-to-core pinning for simulation worker threads.
    ///
    /// PLATFORM SUPPORT
    ///   Windows — <c>SetThreadAffinityMask</c> hard-pins the calling thread
    ///             to a specific logical processor.
    ///   Linux   — <c>sched_setaffinity</c> sets the CPU affinity mask for the
    ///             calling thread.  May fail with EPERM on restricted kernels
    ///             (e.g. snap confinement, hardened Android), which is treated
    ///             as a non-fatal no-op.
    ///   macOS   — Not supported.  macOS provides only affinity tags
    ///             (<c>THREAD_AFFINITY_POLICY</c>) which are scheduler hints,
    ///             not hard pins.
    ///
    /// This is a best-effort facility: callers must not depend on pinning succeeding. The simulation is correct regardless of
    /// whether threads are pinned — pinning is purely a cache-affinity optimisation.
    ///
    /// LIMITATIONS
    ///   The current implementation supports up to 64 logical cores (one
    ///   processor group on Windows, a single <c>ulong</c> mask on Linux).
    ///   Systems with more than 64 cores would need
    ///   <c>SetThreadGroupAffinity</c> on Windows or a larger
    ///   <c>cpu_set_t</c> on Linux.
    ///
    /// TODO: macOS support via <c>thread_policy_set</c> with
    ///       <c>THREAD_AFFINITY_POLICY</c> for hint-based co-location.
    ///
    /// IMPLEMENTATION
    ///   P/Invoke declarations live in <see cref="ThreadPinningNative"/> (non-generic),
    ///   because <c>[LibraryImport]</c> cannot be used inside generic types.
    /// </summary>
    internal sealed partial class ThreadWorker
    {
        private static class ThreadPinning
        {
            public static bool TryPinCurrentThread(int coreIndex) =>
                ThreadPinningNative.TryPinCurrentThread(coreIndex);
        }
    }
}
