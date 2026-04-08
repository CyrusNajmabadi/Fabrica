using System.Runtime.InteropServices;

namespace Fabrica.Core.Threading;

public static partial class ThreadPinningNative
{
    private static bool TryPinWindows(int coreIndex)
    {
        var mask = (nint)(1UL << coreIndex);
        return SetThreadAffinityMask(GetCurrentThread(), mask) != 0;
    }

    [LibraryImport("kernel32")]
    private static partial nint SetThreadAffinityMask(nint hThread, nint dwThreadAffinityMask);

    [LibraryImport("kernel32")]
    private static partial nint GetCurrentThread();
}
