using System.Runtime.InteropServices;

namespace Fabrica.Core.Threading;

/// <summary>
/// Detects the number of performance (P) cores on the current platform. On heterogeneous
/// architectures (Apple Silicon, Intel Alder Lake+, ARM big.LITTLE), P-cores are significantly
/// faster than efficiency (E) cores. For barrier-heavy DAG schedulers, a slow E-core straggler
/// extends the entire phase — using fewer fast P-cores yields better latency and lower variance
/// than using all cores.
///
/// PLATFORM DETECTION
///
///   macOS (Apple Silicon):
///     <c>sysctlbyname("hw.perflevel0.logicalcpu")</c> returns the P-core count directly.
///     perflevel0 is always the highest-performance level.
///
///   Windows (Intel Alder Lake+):
///     <c>GetSystemCpuSetInformation</c> returns per-core <c>EfficiencyClass</c>. The highest
///     efficiency class corresponds to P-cores. Falls back to <c>Environment.ProcessorCount</c>
///     on older homogeneous CPUs.
///
///   Linux:
///     <c>/sys/devices/system/cpu/cpu{N}/cpu_capacity</c> reports per-core capacity (0–1024).
///     Cores at the maximum capacity are P-cores. Falls back to <c>Environment.ProcessorCount</c>
///     if the sysfs entries are absent (homogeneous CPU).
/// </summary>
public static partial class ProcessorTopology
{
    private static readonly int s_performanceCoreCount = DetectPerformanceCoreCount();

    /// <summary>
    /// Number of performance (P) cores on this machine. On homogeneous CPUs, this equals
    /// <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public static int PerformanceCoreCount => s_performanceCoreCount;

    private static int DetectPerformanceCoreCount()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return DetectMacOS();

            if (OperatingSystem.IsWindows())
                return DetectWindows();

            if (OperatingSystem.IsLinux())
                return DetectLinux();
        }
        catch
        {
        }

        return Environment.ProcessorCount;
    }

    // ── macOS ────────────────────────────────────────────────────────────────

    private static int DetectMacOS()
    {
        var value = 0;
        var size = (nint)sizeof(int);
        if (SysctlByName("hw.perflevel0.logicalcpu", ref value, ref size, nint.Zero, 0) == 0 && value > 0)
            return value;

        return Environment.ProcessorCount;
    }

    [LibraryImport("libSystem.B.dylib", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SysctlByName(string name, ref int oldp, ref nint oldlenp, nint newp, nint newlen);

    // ── Windows ──────────────────────────────────────────────────────────────

    private static int DetectWindows()
    {
        // GetSystemCpuSetInformation returns SYSTEM_CPU_SET_INFORMATION structs, each 32 bytes.
        // The EfficiencyClass field at offset 24 distinguishes P-cores (highest class) from E-cores.
        GetSystemCpuSetInformation(nint.Zero, 0, out var returnedLength, nint.Zero, 0);
        if (returnedLength == 0)
            return Environment.ProcessorCount;

        var buffer = new byte[returnedLength];
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                if (!GetSystemCpuSetInformation((nint)ptr, returnedLength, out returnedLength, nint.Zero, 0))
                    return Environment.ProcessorCount;
            }
        }

        byte maxEfficiency = 0;
        var count = 0;
        const int StructSize = 32;
        const int EfficiencyClassOffset = 24;

        for (var offset = 0; offset + StructSize <= returnedLength; offset += StructSize)
        {
            var eff = buffer[offset + EfficiencyClassOffset];
            if (eff > maxEfficiency)
                maxEfficiency = eff;
        }

        for (var offset = 0; offset + StructSize <= returnedLength; offset += StructSize)
        {
            if (buffer[offset + EfficiencyClassOffset] == maxEfficiency)
                count++;
        }

        return count > 0 ? count : Environment.ProcessorCount;
    }

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(
        nint information, uint bufferLength, out uint returnedLength, nint process, uint flags);

    // ── Linux ────────────────────────────────────────────────────────────────

    private static int DetectLinux()
    {
        // cpu_capacity sysfs entries are present on heterogeneous ARM (big.LITTLE) and some
        // Intel hybrid kernels. Each file contains an integer 0–1024 (1024 = fastest).
        var totalCores = Environment.ProcessorCount;
        var capacities = new int[totalCores];
        var anyFound = false;

        for (var i = 0; i < totalCores; i++)
        {
            var path = $"/sys/devices/system/cpu/cpu{i}/cpu_capacity";
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var cap))
            {
                capacities[i] = cap;
                anyFound = true;
            }
        }

        if (!anyFound)
            return totalCores;

        var maxCapacity = 0;
        for (var i = 0; i < totalCores; i++)
        {
            if (capacities[i] > maxCapacity)
                maxCapacity = capacities[i];
        }

        var perfCount = 0;
        for (var i = 0; i < totalCores; i++)
        {
            if (capacities[i] == maxCapacity)
                perfCount++;
        }

        return perfCount > 0 ? perfCount : totalCores;
    }
}
