using System.Runtime.InteropServices;

namespace Fabrica.Core.Jobs;

internal enum JobSource : byte
{
    Local,
    Steal,
    Injection,
}

/// <summary>
/// One record per job execution, captured when instrumentation is enabled on a worker.
/// Two timestamps per job: obtained (after dequeue) and completed (after execute + propagate).
/// The idle gap between consecutive jobs on the same worker is
/// <c>record[i].ObtainedTs - record[i-1].CompletedTs</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SchedulerRecord
{
    public long ObtainedTs;
    public long CompletedTs;
    public JobSource Source;
    public byte ReadiedCount;
    public byte WorkerIndex;
}
