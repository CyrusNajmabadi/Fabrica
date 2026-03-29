using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// All mutable state shared across the simulation and consumption threads.
/// Each field documents which thread writes and which reads.
/// </summary>
internal sealed class SharedState
{
    // Written by simulation thread, read by consumption thread.
    // Volatile: consumption thread always sees the latest published snapshot.
    private volatile WorldSnapshot? _latestSnapshot;
    public WorldSnapshot? LatestSnapshot
    {
        get => _latestSnapshot;
        set => _latestSnapshot = value;
    }

    // Written by consumption thread, read by simulation thread.
    // Semantics: simulation may free any snapshot whose tick < RendererEpoch.
    // Initial value 0: nothing is eligible for freeing until the renderer has consumed something.
    private volatile int _rendererEpoch = 0;
    public int RendererEpoch
    {
        get => _rendererEpoch;
        set => _rendererEpoch = value;
    }

    // Written by save task (on completion), read by consumption thread.
    // Semantics: when current tick >= NextSaveAtTick and the value is > 0, trigger a save.
    // 0 = no save scheduled (cleared by consumption thread before firing; reset by save task when done).
    private volatile int _nextSaveAtTick = SimConstants.SaveIntervalTicks;
    public int NextSaveAtTick
    {
        get => _nextSaveAtTick;
        set => _nextSaveAtTick = value;
    }
}
