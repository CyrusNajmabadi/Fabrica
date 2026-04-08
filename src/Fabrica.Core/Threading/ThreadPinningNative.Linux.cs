using System.Runtime.InteropServices;

namespace Fabrica.Core.Threading;

public static partial class ThreadPinningNative
{
    private static bool TryPinLinux(int coreIndex)
    {
        var mask = 1UL << coreIndex;
        return SchedSetAffinity(0, sizeof(ulong), ref mask) == 0;
    }

    /// <summary>
    /// Reads back the current thread's affinity mask on Linux. Returns false on other platforms.
    /// </summary>
    internal static bool TryGetCurrentThreadAffinity(out ulong affinityMask)
    {
        affinityMask = 0;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                ulong mask = 0;
                if (SchedGetAffinity(0, sizeof(ulong), ref mask) == 0)
                {
                    affinityMask = mask;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    [LibraryImport("libc", EntryPoint = "sched_setaffinity")]
    private static partial int SchedSetAffinity(int pid, nint cpusetsize, ref ulong mask);

    [LibraryImport("libc", EntryPoint = "sched_getaffinity")]
    private static partial int SchedGetAffinity(int pid, nint cpusetsize, ref ulong mask);
}
