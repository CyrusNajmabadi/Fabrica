using System.Diagnostics;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// All mutable state shared across the production and consumption threads.
/// Each member documents which thread(s) may write and which may read.
///
/// VOLATILE MEMORY MODEL
///   A volatile write is a release fence: all writes by the writing thread
///   that precede it are guaranteed visible to any thread that subsequently
///   performs the matching volatile read (an acquire fence).
///
///   Concretely for LatestNode: the production thread fully initialises
///   the node's payload, then volatile-writes LatestNode.  The consumption
///   thread volatile-reads LatestNode, then reads payload fields.
///   The release/acquire pair ensures all payload writes are visible — no
///   additional synchronisation is needed to read payload data.
///
/// CONSERVATIVE RACE DIRECTIONS
///   LatestNode: if the consumption thread reads a slightly stale pointer
///   it merely consumes the same node twice — correct, not stale data.
///
///   ConsumptionEpoch: if the production loop reads a slightly stale (lower)
///   epoch it retains a node one extra cleanup pass — never frees prematurely.
///
///   NextSaveAtTick: see field comment below.
///
/// #if DEBUG ASSERTIONS
///   Each single-writer field uses AssertSingleWriter to catch accidental
///   writes from the wrong thread in debug builds.  This fires on the first
///   write from a new thread ID, so tests that use both fields from the test
///   thread will correctly bind the "owner" to the test thread.
/// </summary>
internal sealed class SharedState<TNode> where TNode : ChainNode<TNode>
{
    // ── LatestNode ───────────────────────────────────────────────────────────
    // Written by production thread only.  Read by consumption thread.

    private volatile TNode? _latestNode;

#if DEBUG
    private int _latestNodeWriterThreadId = -1;
#endif

    public TNode? LatestNode
    {
        get => _latestNode;
        set
        {
#if DEBUG
            AssertSingleWriter(ref _latestNodeWriterThreadId, nameof(this.LatestNode));
#endif
            _latestNode = value;
        }
    }

    // ── ConsumptionEpoch ──────────────────────────────────────────────────────
    // Written by consumption thread only.  Read by production thread.
    // Production may free any node whose sequence < ConsumptionEpoch.
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
            AssertSingleWriter(ref _consumptionEpochWriterThreadId, nameof(this.ConsumptionEpoch));
#endif
            _consumptionEpoch = value;
        }
    }

    // ── NextSaveAtTick ────────────────────────────────────────────────────────
    // Written by consumption thread (sets to 0 before dispatch) AND by the save
    // task (sets to tick + SaveIntervalTicks on completion).
    // Read by the consumption thread.
    //
    // Two-writer safety: the writes do not conflict in practice.  The consumption
    // thread writes 0 only when the field is non-zero (a save is due); the save task
    // writes a positive value only after the consumption thread has written 0 and
    // the save has completed.  The worst-case race is the save task writing the next
    // interval at the same moment the consumption thread is checking the value — that
    // is benign because the consumption thread will see the 0 it just wrote, and the
    // save task's write of the next tick will stand as the new schedule.
    //
    // Initial value = SaveIntervalTicks so the first save fires at tick 12000 (5 min).

    private volatile int _nextSaveAtTick = SimulationConstants.SaveIntervalTicks;

    public int NextSaveAtTick
    {
        get => _nextSaveAtTick;
        set => _nextSaveAtTick = value;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

#if DEBUG
    private static void AssertSingleWriter(ref int storedThreadId, string propertyName)
    {
        var current = Environment.CurrentManagedThreadId;
        if (storedThreadId == -1)
            storedThreadId = current;
        Debug.Assert(
            storedThreadId == current,
            $"{propertyName} written from thread {current} but expected thread {storedThreadId}.");
    }
#endif
}
