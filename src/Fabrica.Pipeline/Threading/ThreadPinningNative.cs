using System.Runtime.InteropServices;

namespace Fabrica.Pipeline.Threading;

/// <summary>
/// Native interop for thread-to-core pinning. Declared at namespace scope because <c>[LibraryImport]</c> cannot be applied inside
/// generic types (CS7042). Called from <see cref="WorkerGroup{TState,TExecutor}.ThreadWorker"/>.
/// </summary>
internal static partial class ThreadPinningNative
{
    /// <summary>
    /// Attempts to pin the calling thread to the specified logical core. Returns true if the OS accepted the request, false
    /// otherwise. Safe to call on any platform — unsupported platforms return false.
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
            // P/Invoke failed — permission denied, library not found, containerised environment, etc. Thread pinning is optional.
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
        return SchedSetAffinity(0, sizeof(ulong), ref mask) == 0;
    }

    [LibraryImport("libc", EntryPoint = "sched_setaffinity")]
    private static partial int SchedSetAffinity(int pid, nint cpusetsize, ref ulong mask);
}
