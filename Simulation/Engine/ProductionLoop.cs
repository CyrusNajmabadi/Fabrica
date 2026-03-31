using System.Diagnostics;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The production ("producer") thread.  Advances the chain one node at a time,
/// delegates domain-specific work to a <typeparamref name="TProducer"/>, and
/// reclaims nodes the consumption thread has finished with.
///
/// CHAIN MODEL
///   Every tick appends a new <typeparamref name="TNode"/> to a singly-linked
///   forward chain:
///
///     _oldestNode → [seq 1] → [seq 2] → … → [seq N] ← _currentNode
///                                                       (= LatestNode)
///
///   The production loop is the sole writer to the chain.  Domain payload
///   is written before the volatile-write to LatestNode (a release fence),
///   so the consumption thread always sees fully-initialised data (acquire
///   fence on the matching read).
///
/// EPOCH-BASED RECLAMATION
///   After each tick, <see cref="CleanupStaleNodes"/> reads ConsumptionEpoch
///   (volatile acquire) and walks _oldestNode forward, freeing every node
///   whose sequence is strictly less than the epoch and is not pinned.
///
///   Safety argument: ConsumptionEpoch = N means the consumption thread has
///   finished processing tick N.  The production loop reads this volatile
///   value; if it sees a stale (lower) epoch it merely retains a node one
///   extra cleanup pass — never frees prematurely.
///
/// PINNED NODES AND THE PINNED QUEUE
///   A node being saved must not be reclaimed while the save task reads it.
///   When <see cref="CleanupStaleNodes"/> encounters a pinned node it cannot
///   free, it calls ClearNext() to sever the node from the live chain and
///   parks it in _pinnedQueue.  This lets _oldestNode advance past the pinned
///   node so that everything after it can still be freed normally.
///   The pinned queue is drained each cleanup pass once the pin is released.
///
/// BACKPRESSURE
///   The sequence-epoch gap (in nanoseconds) drives two pressure levels:
///
///   1. Soft pressure — when the gap exceeds PressureLowWaterMarkNanoseconds,
///      an exponentially increasing delay (1 ms → 64 ms) is inserted before
///      each tick, slowing production and giving the consumption thread
///      time to advance its epoch.
///
///   2. Hard ceiling — when the gap reaches PressureHardCeilingNanoseconds,
///      the loop blocks entirely, sleeping PressureMaxDelayNanoseconds
///      per iteration until the gap drops below the ceiling.  This bounds
///      memory growth: production cannot run arbitrarily far ahead.
///
/// Generic on all type parameters (all constrained to struct) so the JIT/AOT
/// devirtualises all calls — zero interface-dispatch overhead in the hot path.
/// </summary>
internal sealed class ProductionLoop<TNode, TProducer, TClock, TWaiter>
    where TNode : ChainNode<TNode>, new()
    where TProducer : struct, IProducer<TNode>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly ObjectPool<TNode> _nodePool;
    private readonly PinnedVersions _pinnedVersions;
    private readonly SharedState<TNode> _shared;
    private TProducer _producer;
    private readonly TClock _clock;
    private readonly TWaiter _waiter;

    private int _currentSequence;
    private TNode? _currentNode;
    private TNode? _oldestNode;

    private readonly HashSet<TNode> _pinnedQueue = new();

    public ProductionLoop(
        ObjectPool<TNode> nodePool,
        PinnedVersions pinnedVersions,
        SharedState<TNode> shared,
        TProducer producer,
        TClock clock,
        TWaiter waiter)
    {
        _nodePool = nodePool;
        _pinnedVersions = pinnedVersions;
        _shared = shared;
        _producer = producer;
        _clock = clock;
        _waiter = waiter;
    }

    public void Run(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        this.Bootstrap();

        var lastTime = _clock.NowNanoseconds;
        long accumulator = 0;

        while (!cancellationToken.IsCancellationRequested)
            this.RunOneIteration(cancellationToken, ref lastTime, ref accumulator);
    }

    private void RunOneIteration(
        CancellationToken cancellationToken,
        ref long lastTime,
        ref long accumulator)
    {
        var now = _clock.NowNanoseconds;
        var delta = Math.Max(0, now - lastTime);
        lastTime = now;
        accumulator += delta;

        this.ProcessAvailableTicks(cancellationToken, ref accumulator);

        _waiter.Wait(new TimeSpan(SimulationConstants.IdleYieldNanoseconds / 100), cancellationToken);
    }

    private void ProcessAvailableTicks(CancellationToken cancellationToken, ref long accumulator)
    {
        while (accumulator >= SimulationConstants.TickDurationNanoseconds)
        {
            this.ApplyPressureDelay(cancellationToken);
            this.Tick(cancellationToken);
            this.CleanupStaleNodes();
            accumulator -= SimulationConstants.TickDurationNanoseconds;
        }
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    /// <summary>
    /// Allocates the sequence-0 node, anchors both _oldestNode and
    /// _currentNode to it, and publishes it via LatestNode so the
    /// consumption thread has something to process before the first tick.
    /// </summary>
    private void Bootstrap()
    {
        var node = _nodePool.Rent();

        _currentSequence = 0;
        node.InitializeBase(0);
        _producer.Bootstrap(node, CancellationToken.None);

        _currentNode = node;
        _oldestNode = node;

        node.MarkPublished(_clock.NowNanoseconds);
        _shared.LatestNode = node;
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the chain by one node.
    ///
    /// Rents a fresh node (always succeeds — the pool grows on demand),
    /// initialises it, delegates payload production to the producer, links
    /// it onto the chain via SetNext, and volatile-writes it to LatestNode
    /// (release fence — makes all payload writes visible to the consumption
    /// thread's subsequent acquire-read).
    /// </summary>
    private void Tick(CancellationToken cancellationToken)
    {
        var node = _nodePool.Rent();

        _currentSequence++;
        node.InitializeBase(_currentSequence);
        _producer.Produce(_currentNode!, node, cancellationToken);

        _currentNode!.SetNext(node);
        _currentNode = node;

        node.MarkPublished(_clock.NowNanoseconds);
        _shared.LatestNode = node;
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reclaims nodes the consumption thread has moved past.
    ///
    /// Pass 1 — live chain walk:
    ///   Reads ConsumptionEpoch once (volatile acquire).  Walks _oldestNode
    ///   forward while sequence &lt; epoch and node != _currentNode.  Each node:
    ///     - Not pinned → FreeNode immediately.
    ///     - Pinned → ClearNext (severs from chain) then park in _pinnedQueue.
    ///   Severing a pinned node lets _oldestNode advance past it so the
    ///   nodes after it are not blocked from reclamation.
    ///
    /// Pass 2 — pinned queue drain:
    ///   Scans _pinnedQueue and frees any node whose pin has since cleared.
    ///
    /// Invariant: _currentNode is never freed here.  The loop guard
    /// (_oldestNode != _currentNode) always leaves the latest published
    /// node alive regardless of the epoch value.
    /// </summary>
    private void CleanupStaleNodes()
    {
        var consumptionEpoch = _shared.ConsumptionEpoch; // volatile read

        while (_oldestNode is not null
               && _oldestNode != _currentNode
               && _oldestNode.SequenceNumber < consumptionEpoch)
        {
            var toProcess = _oldestNode;
            _oldestNode = toProcess.NextInChain;

            if (_pinnedVersions.IsPinned(toProcess.SequenceNumber))
            {
                toProcess.ClearNext();
                if (!_pinnedQueue.Add(toProcess))
                    throw new InvalidOperationException("Pinned node was added to the cleanup queue more than once.");
            }
            else
            {
                this.FreeNode(toProcess);
            }
        }

        _pinnedQueue.RemoveWhere(node =>
        {
            if (_pinnedVersions.IsPinned(node.SequenceNumber))
                return false;
            this.FreeNode(node);
            return true;
        });
    }

    private void FreeNode(TNode node)
    {
        _producer.ReleaseResources(node);
        node.Release();
        Debug.Assert(node.IsUnreferenced, "Node still referenced after cleanup — refcount mismatch.");
        _nodePool.Return(node);
    }

    // ── Backpressure ─────────────────────────────────────────────────────────

    /// <summary>
    /// Two-level backpressure gate called before each tick.
    ///
    /// Hard ceiling: if the gap (in nanoseconds) is at or above the hard
    /// ceiling, the loop blocks in a sleep loop until consumption catches up
    /// enough to drop below the ceiling.
    ///
    /// Soft pressure: once below the ceiling, an exponentially increasing
    /// delay is inserted if the gap still exceeds the low water mark.
    /// See <see cref="SimulationPressure.ComputeDelay"/> for the bucket schedule.
    /// </summary>
    private void ApplyPressureDelay(CancellationToken cancellationToken)
    {
        var gapNanoseconds = (long)(_currentSequence - _shared.ConsumptionEpoch)
                            * SimulationConstants.TickDurationNanoseconds;

        while (gapNanoseconds >= SimulationConstants.PressureHardCeilingNanoseconds)
        {
            _waiter.Wait(
                new TimeSpan(SimulationConstants.PressureMaxDelayNanoseconds / 100),
                cancellationToken);
            gapNanoseconds = (long)(_currentSequence - _shared.ConsumptionEpoch)
                           * SimulationConstants.TickDurationNanoseconds;
        }

        var delay = SimulationPressure.ComputeDelay(
            gapNanoseconds,
            SimulationConstants.PressureLowWaterMarkNanoseconds,
            SimulationConstants.TickDurationNanoseconds,
            SimulationConstants.PressureBucketCount,
            SimulationConstants.PressureBaseDelayNanoseconds,
            SimulationConstants.PressureMaxDelayNanoseconds);

        if (delay > 0)
            _waiter.Wait(new TimeSpan(delay / 100), cancellationToken);
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly ProductionLoop<TNode, TProducer, TClock, TWaiter> _loop;

        public TestAccessor(ProductionLoop<TNode, TProducer, TClock, TWaiter> loop) => _loop = loop;

        public void Bootstrap() => _loop.Bootstrap();

        public void Tick() => _loop.Tick(CancellationToken.None);

        public void CleanupStaleNodes() => _loop.CleanupStaleNodes();

        public void RunOneIteration(
            CancellationToken cancellationToken,
            ref long lastTime,
            ref long accumulator) =>
            _loop.RunOneIteration(cancellationToken, ref lastTime, ref accumulator);

        public int CurrentSequence => _loop._currentSequence;

        public TNode? CurrentNode => _loop._currentNode;

        public TNode? OldestNode => _loop._oldestNode;

        public int PinnedQueueCount => _loop._pinnedQueue.Count;

        public void SetOldestNodeForTesting(TNode node) => _loop._oldestNode = node;
    }
}
