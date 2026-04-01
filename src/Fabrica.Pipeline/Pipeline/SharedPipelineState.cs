using System.Diagnostics;

namespace Fabrica.Pipeline;

/// <summary>
/// All mutable state shared across the production and consumption threads. Each member documents which thread(s) may write and
/// which may read.
///
/// MEMORY MODEL
///   Both cross-thread fields use <see cref="Volatile.Read"/> / <see cref="Volatile.Write"/> to make the acquire/release fences
///   explicit at each access site rather than implicit on the field declaration.
///
///   A Volatile.Write is a release fence: all prior writes by the writing thread are guaranteed visible to any thread that
///   subsequently performs the matching Volatile.Read (an acquire fence).
///
///   Concretely for LatestNode: the production thread fully initialises the node's payload, then Volatile.Writes LatestNode. The
///   consumption thread Volatile.Reads LatestNode, then reads payload fields. The release/acquire pair ensures all payload writes
///   are visible — no additional synchronisation is needed.
///
/// CROSS-FIELD INDEPENDENCE
///   The two fields form a feedback loop (producer publishes nodes, consumer advances the epoch to allow reclamation) but do NOT
///   require atomic cross-field reads. Staleness in either direction is conservative:
///
///   LatestNode: a stale read means the consumer re-processes the same node — correct, just redundant.
///
///   ConsumptionEpoch: a stale (lower) read means the producer retains a node one extra cleanup pass — never frees prematurely.
///
///   Because each field's correctness is independent of reading the other field's latest value in the same operation, no lock or
///   combined atomic is needed.
///
/// PinnedVersions
///   Thread-safe registry of snapshot sequences that deferred consumers are
///   still using.  Consumption thread pins before dispatching; threadpool
///   tasks unpin on completion; production thread reads IsPinned during
///   cleanup.  See <see cref="PinnedVersions"/> for full concurrency details.
///
/// #if DEBUG ASSERTIONS
///   Each single-writer field uses AssertSingleWriter to catch accidental
///   writes from the wrong thread in debug builds.  This fires on the first
///   write from a new thread ID, so tests that use both fields from the test
///   thread will correctly bind the "owner" to the test thread.
/// </summary>
public sealed class SharedPipelineState<TPayload>
{
    /// <summary>
    /// Thread-safe. Written by consumption thread and threadpool tasks. Read by production thread during cleanup.
    /// </summary>
    public readonly PinnedVersions PinnedVersions = new();

    /// <summary>
    /// Written by production thread only (release).  Read by consumption thread (acquire).
    /// </summary>
    private BaseProductionLoop<TPayload>.ChainNode? _latestNode;

    /// <summary>
    /// Written by consumption thread only (release). Read by production thread (acquire). Production may free any node whose
    /// sequence &lt; ConsumptionEpoch. Initial value 0: nothing eligible for freeing until consumption has processed something.
    /// </summary>
    private int _consumptionEpoch;

#if DEBUG
    private int _latestNodeWriterThreadId = -1;
    private int _consumptionEpochWriterThreadId = -1;

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

    public BaseProductionLoop<TPayload>.ChainNode? LatestNode
    {
        get => Volatile.Read(ref _latestNode);
        set
        {
#if DEBUG
            AssertSingleWriter(ref _latestNodeWriterThreadId, nameof(this.LatestNode));
#endif
            Volatile.Write(ref _latestNode, value);
        }
    }

#if DEBUG
#endif

    public int ConsumptionEpoch
    {
        get => Volatile.Read(ref _consumptionEpoch);
        set
        {
#if DEBUG
            AssertSingleWriter(ref _consumptionEpochWriterThreadId, nameof(this.ConsumptionEpoch));
#endif
            Volatile.Write(ref _consumptionEpoch, value);
        }
    }
}
