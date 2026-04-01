using System.Diagnostics;
using Engine.Memory;

namespace Engine.Pipeline;

/// <summary>
/// Abstract base class that owns the forward-linked chain of nodes and all mutation of those nodes. <see cref="ChainNode"/> is a
/// nested type so that in DEBUG builds its mutation surface can be hidden behind a private derived type
/// (<c>PrivateChainNode</c>), giving compile-time enforcement that only the production loop can mutate nodes. In RELEASE the two
/// types collapse into a single class via <c>partial</c> with zero overhead.
///
/// The derived <see cref="ProductionLoop{TPayload,TProducer,TClock,TWaiter}"/> adds the tick loop, backpressure, and
/// domain-specific producer/consumer coordination. It calls the protected chain helpers defined here.
/// </summary>
internal abstract partial class BaseProductionLoop<TPayload>(
    ObjectPool<BaseProductionLoop<TPayload>.ChainNode, BaseProductionLoop<TPayload>.ChainNode.Allocator> nodePool,
    PinnedVersions pinnedVersions)
{
    // ════════════════════════════ CHAIN STATE ═════════════════════════════════

    private readonly ObjectPool<ChainNode, ChainNode.Allocator> _nodePool = nodePool;
    private readonly PinnedVersions _pinnedVersions = pinnedVersions;
    private readonly HashSet<ChainNode> _pinnedQueue = [];
    private int _currentSequence;
    private ChainNode? _currentNode;
    private ChainNode? _oldestNode;

    // ══════════════════════════ ABSTRACT HOOK ═════════════════════════════════

    /// <summary>
    /// Release domain-specific resources from a payload being freed (e.g. return a WorldImage to its pool). Called on the
    /// production thread during cleanup.
    /// </summary>
    protected abstract void ReleasePayloadResources(TPayload payload);

    // ═══════════════════════ PROTECTED CHAIN HELPERS ══════════════════════════

    /// <summary>Current (most recent) node in the chain. Read-only for derived classes.</summary>
    protected ChainNode? CurrentNode => _currentNode;

    /// <summary>Current sequence number.</summary>
    protected int CurrentSequence => _currentSequence;

    /// <summary>
    /// Allocates the sequence-0 node, sets both anchors, and returns it. The caller is responsible for publishing to shared
    /// state.
    /// </summary>
    protected ChainNode BootstrapChain(TPayload payload, long publishTimeNanoseconds)
    {
        var node = _nodePool.Rent();
        _currentSequence = 0;

        var mutation = Mutate(node);
        mutation.InitializeBase(0);
        mutation.SetPayload(payload);

        _currentNode = node;
        _oldestNode = node;

        mutation.MarkPublished(publishTimeNanoseconds);
        return node;
    }

    /// <summary>
    /// Appends a new node to the chain and returns it. The caller is responsible for publishing to shared state.
    /// </summary>
    protected ChainNode AppendToChain(TPayload payload, long publishTimeNanoseconds)
    {
        var node = _nodePool.Rent();
        _currentSequence++;

        var mutation = Mutate(node);
        mutation.InitializeBase(_currentSequence);
        mutation.SetPayload(payload);

        Mutate(_currentNode!).SetNext(node);
        _currentNode = node;

        mutation.MarkPublished(publishTimeNanoseconds);
        return node;
    }

    /// <summary>
    /// Reclaims nodes the consumption thread has moved past.
    /// </summary>
    protected void CleanupStaleNodes(int consumptionEpoch)
    {
        while (_oldestNode is not null
               && _oldestNode != _currentNode
               && _oldestNode.SequenceNumber < consumptionEpoch)
        {
            var toProcess = _oldestNode;
            _oldestNode = Mutate(toProcess).GetNext();

            if (_pinnedVersions.IsPinned(toProcess.SequenceNumber))
            {
                Mutate(toProcess).ClearNext();
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

    private void FreeNode(ChainNode node)
    {
        this.ReleasePayloadResources(node.Payload);
        var mutation = Mutate(node);
        mutation.ClearPayload();
        mutation.Release();
        Debug.Assert(node.IsUnreferenced, "Node still referenced after cleanup — refcount mismatch.");
        _nodePool.Return(node);
    }
}
