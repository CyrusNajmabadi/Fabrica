using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// All mutable state shared across the simulation and consumption threads.
/// Each field documents which thread writes and which reads.
///
/// Memory-model note on volatile reference fields:
///   A volatile write is a release fence and a volatile read is an acquire fence.
///   This means all writes performed by the writing thread before the volatile write
///   are guaranteed visible to any thread that subsequently performs the volatile read.
///   Concretely: the simulation thread writes WorldImage fields, then volatile-writes
///   LatestSnapshot; the consumption thread volatile-reads LatestSnapshot, then reads
///   WorldImage fields — all WorldImage writes are visible.  This is safe because
///   WorldImage is immutable once published.
/// </summary>
internal sealed class SharedState
{
    // Written by simulation thread, read by consumption thread.
    public volatile WorldSnapshot? LatestSnapshot;

    // Written by consumption thread, read by simulation thread.
    // Simulation may free any snapshot whose tick < RendererEpoch.
    // Initial value 0: nothing is eligible for freeing until the renderer has consumed something.
    public volatile int RendererEpoch = 0;

    // Written by save task on completion, read by consumption thread.
    // 0 = no save scheduled.
    // Consumption thread clears this to 0 before firing a save; save task resets it on completion.
    public volatile int NextSaveAtTick = SimulationConstants.SaveIntervalTicks;
}
