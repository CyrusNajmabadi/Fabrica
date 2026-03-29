using System.Diagnostics;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// All mutable state shared across the simulation and consumption threads.
/// Each member documents which thread(s) may write and which may read.
///
/// Memory-model note on volatile reference fields:
///   A volatile write is a release fence and a volatile read is an acquire fence.
///   All writes made by the writing thread before the volatile write are guaranteed
///   visible to any thread that subsequently performs the volatile read.
///   Concretely: the simulation thread writes WorldImage fields, then volatile-writes
///   LatestSnapshot.  The consumption thread volatile-reads LatestSnapshot, then reads
///   WorldImage fields — all WorldImage writes are visible.  This holds because
///   WorldImage is immutable once published.
/// </summary>
internal sealed class SharedState
{
    // ── LatestSnapshot ────────────────────────────────────────────────────────
    // Written by simulation thread only.  Read by consumption thread.

    private volatile WorldSnapshot? _latestSnapshot;

#if DEBUG
    private int _latestSnapshotWriterThreadId = -1;
#endif

    public WorldSnapshot? LatestSnapshot
    {
        get => _latestSnapshot;
        set
        {
#if DEBUG
            AssertSingleWriter(ref _latestSnapshotWriterThreadId, nameof(LatestSnapshot));
#endif
            _latestSnapshot = value;
        }
    }

    // ── ConsumptionEpoch ──────────────────────────────────────────────────────
    // Written by consumption thread only.  Read by simulation thread.
    // Simulation may free any snapshot whose tick < ConsumptionEpoch.
    // Initial value 0: nothing eligible for freeing until consumption has processed something.

    private volatile int _consumptionEpoch;

#if DEBUG
    private int _consumptionEpochWriterThreadId = -1;
#endif

    public int ConsumptionEpoch
    {
        get => _consumptionEpoch;
        set
        {
#if DEBUG
            AssertSingleWriter(ref _consumptionEpochWriterThreadId, nameof(ConsumptionEpoch));
#endif
            _consumptionEpoch = value;
        }
    }

    // ── NextSaveAtTick ────────────────────────────────────────────────────────
    // Written by both consumption thread (to clear before firing a save) and save task
    // (to reset on completion).  Multiple writers — no single-writer invariant to assert.
    // Read by consumption thread.

    public volatile int NextSaveAtTick = SimulationConstants.SaveIntervalTicks;

    // ── Helpers ───────────────────────────────────────────────────────────────

#if DEBUG
    private static void AssertSingleWriter(ref int storedThreadId, string propertyName)
    {
        int current = Environment.CurrentManagedThreadId;
        if (storedThreadId == -1)
            storedThreadId = current;
        Debug.Assert(
            storedThreadId == current,
            $"{propertyName} written from thread {current} but expected thread {storedThreadId}.");
    }
#endif
}
