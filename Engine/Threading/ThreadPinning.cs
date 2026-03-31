using System.Runtime.InteropServices;

namespace Engine.Threading;

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
/// This is a best-effort facility: callers must not depend on pinning
/// succeeding.  The simulation is correct regardless of whether threads
/// are pinned — pinning is purely a cache-affinity optimisation.
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
/// </summary>
internal static partial class ThreadPinning
{
    /// <summary>
    /// Attempts to pin the calling thread to the specified logical core.
    /// Returns true if the OS accepted the request, false otherwise.
    /// Safe to call on any platform — unsupported platforms return false.
    /// </summary>
    public static bool TryPinCurrentThread(int coreIndex)
    {
        if ((uint)coreIndex >= (uint)Environment.ProcessorCount)
            return false;

        try
        {
            if (OperatingSystem.IsWindows())
                return TryPinWindows(coreIndex);
            if (OperatingSystem.IsLinux())
                return TryPinLinux(coreIndex);
        }
        catch
        {
            // P/Invoke failed — permission denied, library not found,
            // containerised environment, etc.  Thread pinning is optional.
        }

        return false;
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    private static bool TryPinWindows(int coreIndex)
    {
        var mask = (nint)(1UL << coreIndex);
        return SetThreadAffinityMask(GetCurrentThread(), mask) != 0;
    }

    [LibraryImport("kernel32")]
    private static partial nint SetThreadAffinityMask(nint hThread, nint dwThreadAffinityMask);

    [LibraryImport("kernel32")]
    private static partial nint GetCurrentThread();

    // ── Linux ────────────────────────────────────────────────────────────────

    private static bool TryPinLinux(int coreIndex)
    {
        var mask = 1UL << coreIndex;
        return SchedSetAffinity(0, (nint)sizeof(ulong), ref mask) == 0;
    }

    [LibraryImport("libc", EntryPoint = "sched_setaffinity")]
    private static partial int SchedSetAffinity(int pid, nint cpusetsize, ref ulong mask);
}
