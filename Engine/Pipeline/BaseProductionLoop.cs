using System.Diagnostics;
using Engine.Memory;

namespace Engine.Pipeline;

/// <summary>
/// Abstract base class that owns the forward-linked chain of nodes and all
/// mutation of those nodes.  <see cref="ChainNode"/> is a nested type so that
/// in DEBUG builds its mutation surface can be hidden behind a private derived
/// type (<c>PrivateChainNode</c>), giving compile-time enforcement that only
/// the production loop can mutate nodes.  In RELEASE the two types collapse
/// into a single sealed class with zero overhead.
///
/// The derived <see cref="ProductionLoop{TPayload,TProducer,TClock,TWaiter}"/>
/// adds the tick loop, backpressure, and domain-specific producer/consumer
/// coordination.  It calls the protected chain helpers defined here.
/// </summary>
internal abstract class BaseProductionLoop<TPayload>
{
    // ═════════════════════════════ CHAIN NODE ═════════════════════════════════

#if DEBUG
    /// <summary>
    /// DEBUG: abstract shell — exposes only the read-only surface.
    /// The concrete <c>PrivateChainNode</c> (private to this class) holds all
    /// state and mutation methods.  Because <c>PrivateChainNode</c> is private,
    /// code outside <see cref="BaseProductionLoop{TPayload}"/> cannot see the
    /// mutation API.  Any accidental use fails to compile.
    /// </summary>
    public abstract class ChainNode
    {
        public abstract int SequenceNumber { get; }
        public abstract long PublishTimeNanoseconds { get; }
        public abstract TPayload Payload { get; }
        internal abstract ChainNode? NextInChain { get; }
        internal abstract bool IsUnreferenced { get; }

        internal static ChainSegment Chain(ChainNode? start, ChainNode end) =>
            new(start ?? end, end);

        internal readonly struct ChainSegment
        {
            private readonly ChainNode _start;
            private readonly ChainNode _end;

            internal ChainSegment(ChainNode start, ChainNode end)
            {
                _start = start;
                _end = end;
            }

            public Enumerator GetEnumerator() => new(_start, _end);

            public struct Enumerator
            {
                private readonly ChainNode _end;
                private ChainNode? _current;
                private bool _started;

                internal Enumerator(ChainNode start, ChainNode end)
                {
                    _current = start;
                    _end = end;
                    _started = false;
                }

                public readonly ChainNode Current => _current!;

                public bool MoveNext()
                {
                    if (!_started)
                    {
                        _started = true;
                        return _current is not null;
                    }

                    if (_current is null || ReferenceEquals(_current, _end))
                    {
                        _current = null;
                        return false;
                    }

                    _current = _current.NextInChain;
                    return _current is not null;
                }
            }
        }
    }

    private sealed class PrivateChainNode : ChainNode
    {
        private int _sequenceNumber;
        private long _publishTimeNanoseconds;
        private TPayload _payload = default!;
        private PrivateChainNode? _next;
        private int _refCount;

        public override int SequenceNumber => _sequenceNumber;
        public override long PublishTimeNanoseconds => _publishTimeNanoseconds;
        public override TPayload Payload => _payload;
        internal override ChainNode? NextInChain => _next;
        internal override bool IsUnreferenced => _refCount == 0;

        public void InitializeBase(int sequenceNumber)
        {
            Debug.Assert(_refCount == 0, "InitializeBase called on a node still in use");
            _sequenceNumber = sequenceNumber;
            _publishTimeNanoseconds = 0;
            _next = null;
            _refCount = 1;
        }

        public void SetPayload(TPayload payload) => _payload = payload;

        public void MarkPublished(long timeNanoseconds) => _publishTimeNanoseconds = timeNanoseconds;

        public void SetNext(PrivateChainNode next)
        {
            Debug.Assert(_next is null, "SetNext called more than once on the same node");
            _next = next;
        }

        public void ClearNext() => _next = null;

        public void ClearPayload() => _payload = default!;

        public void AddRef()
        {
            Debug.Assert(_refCount > 0, "AddRef on a zero-refcount (freed) node");
            _refCount++;
        }

        public void Release()
        {
            Debug.Assert(_refCount > 0, "Release called more times than AddRef");
            if (--_refCount == 0)
                _next = null;
        }
    }

#else
    /// <summary>
    /// RELEASE: single sealed class with all members.  In release builds anyone
    /// in the assembly <em>could</em> call the internal mutation methods, but the
    /// DEBUG build (which CI also runs) will catch that at compile time because
    /// those methods do not exist on the abstract <see cref="ChainNode"/>.
    /// </summary>
    public sealed class ChainNode
    {
        public int SequenceNumber { get; private set; }
        public long PublishTimeNanoseconds { get; private set; }
        public TPayload Payload { get; internal set; } = default!;

        private ChainNode? _next;
        private int _refCount;

        internal ChainNode? NextInChain => _next;
        internal bool IsUnreferenced => _refCount == 0;

        internal void InitializeBase(int sequenceNumber)
        {
            Debug.Assert(_refCount == 0, "InitializeBase called on a node still in use");
            this.SequenceNumber = sequenceNumber;
            this.PublishTimeNanoseconds = 0;
            _next = null;
            _refCount = 1;
        }

        internal void MarkPublished(long timeNanoseconds) => this.PublishTimeNanoseconds = timeNanoseconds;

        internal void SetNext(ChainNode next)
        {
            Debug.Assert(_next is null, "SetNext called more than once on the same node");
            _next = next;
        }

        internal void ClearNext() => _next = null;

        internal void ClearPayload() => this.Payload = default!;

        internal void AddRef()
        {
            Debug.Assert(_refCount > 0, "AddRef on a zero-refcount (freed) node");
            _refCount++;
        }

        internal void Release()
        {
            Debug.Assert(_refCount > 0, "Release called more times than AddRef");
            if (--_refCount == 0)
                _next = null;
        }

        internal static ChainSegment Chain(ChainNode? start, ChainNode end) =>
            new(start ?? end, end);

        internal readonly struct ChainSegment
        {
            private readonly ChainNode _start;
            private readonly ChainNode _end;

            internal ChainSegment(ChainNode start, ChainNode end)
            {
                _start = start;
                _end = end;
            }

            public Enumerator GetEnumerator() => new(_start, _end);

            public struct Enumerator
            {
                private readonly ChainNode _end;
                private ChainNode? _current;
                private bool _started;

                internal Enumerator(ChainNode start, ChainNode end)
                {
                    _current = start;
                    _end = end;
                    _started = false;
                }

                public readonly ChainNode Current => _current!;

                public bool MoveNext()
                {
                    if (!_started)
                    {
                        _started = true;
                        return _current is not null;
                    }

                    if (_current is null || ReferenceEquals(_current, _end))
                    {
                        _current = null;
                        return false;
                    }

                    _current = _current.NextInChain;
                    return _current is not null;
                }
            }
        }
    }
#endif

    // ═══════════════════════════ NODE ALLOCATOR ═══════════════════════════════

    /// <summary>
    /// Allocator for the node pool.  Nested here so it can construct
    /// <c>PrivateChainNode</c> in DEBUG builds (the type is private to this class).
    /// </summary>
    internal struct NodeAllocator : IAllocator<ChainNode>
    {
        public ChainNode Allocate() =>
#if DEBUG
            new PrivateChainNode();
#else
            new ChainNode();
#endif

        public void Reset(ChainNode item) { }
    }

    // ═══════════════════════════ NODE MUTATION ════════════════════════════════
    //
    // Private facade that concentrates all #if DEBUG casts in one place.
    // The rest of the base class calls Mutate(node).Method() — ifdef-free.

    private readonly struct NodeMutation
    {
#if DEBUG
        private readonly PrivateChainNode _node;
        public NodeMutation(ChainNode node) => _node = (PrivateChainNode)node;
#else
        private readonly ChainNode _node;
        public NodeMutation(ChainNode node) => _node = node;
#endif

        public void InitializeBase(int seq) => _node.InitializeBase(seq);
        public void MarkPublished(long time) => _node.MarkPublished(time);
        public void ClearNext() => _node.ClearNext();
        public void ClearPayload() => _node.ClearPayload();
        public void AddRef() => _node.AddRef();
        public void Release() => _node.Release();

        public void SetPayload(TPayload payload) =>
#if DEBUG
            _node.SetPayload(payload);
#else
            _node.Payload = payload;
#endif


        public void SetNext(ChainNode next) =>
#if DEBUG
            _node.SetNext((PrivateChainNode)next);
#else
            _node.SetNext(next);
#endif

    }

    private static NodeMutation Mutate(ChainNode node) => new(node);

    // ════════════════════════════ CHAIN STATE ═════════════════════════════════

    private readonly ObjectPool<ChainNode, NodeAllocator> _nodePool;
    private readonly PinnedVersions _pinnedVersions;
    private int _currentSequence;
    private ChainNode? _currentNode;
    private ChainNode? _oldestNode;
    private readonly HashSet<ChainNode> _pinnedQueue = new();

    protected BaseProductionLoop(
        ObjectPool<ChainNode, NodeAllocator> nodePool,
        PinnedVersions pinnedVersions)
    {
        _nodePool = nodePool;
        _pinnedVersions = pinnedVersions;
    }

    // ══════════════════════════ ABSTRACT HOOK ═════════════════════════════════

    /// <summary>
    /// Release domain-specific resources from a payload being freed (e.g. return
    /// a WorldImage to its pool).  Called on the production thread during cleanup.
    /// </summary>
    protected abstract void ReleasePayloadResources(TPayload payload);

    // ═══════════════════════ PROTECTED CHAIN HELPERS ══════════════════════════

    /// <summary>Current (most recent) node in the chain. Read-only for derived classes.</summary>
    protected ChainNode? CurrentNode => _currentNode;

    /// <summary>Current sequence number.</summary>
    protected int CurrentSequence => _currentSequence;

    /// <summary>
    /// Allocates the sequence-0 node, sets both anchors, and returns it.
    /// The caller is responsible for publishing to shared state.
    /// </summary>
    protected ChainNode BootstrapChain(TPayload payload, long publishTimeNanoseconds)
    {
        var node = _nodePool.Rent();
        _currentSequence = 0;

        var mut = Mutate(node);
        mut.InitializeBase(0);
        mut.SetPayload(payload);

        _currentNode = node;
        _oldestNode = node;

        mut.MarkPublished(publishTimeNanoseconds);
        return node;
    }

    /// <summary>
    /// Appends a new node to the chain and returns it.
    /// The caller is responsible for publishing to shared state.
    /// </summary>
    protected ChainNode AppendToChain(TPayload payload, long publishTimeNanoseconds)
    {
        var node = _nodePool.Rent();
        _currentSequence++;

        var mut = Mutate(node);
        mut.InitializeBase(_currentSequence);
        mut.SetPayload(payload);

        Mutate(_currentNode!).SetNext(node);
        _currentNode = node;

        mut.MarkPublished(publishTimeNanoseconds);
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
            _oldestNode = toProcess.NextInChain;

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
        var mut = Mutate(node);
        mut.ClearPayload();
        mut.Release();
        Debug.Assert(node.IsUnreferenced, "Node still referenced after cleanup — refcount mismatch.");
        _nodePool.Return(node);
    }

    // ═══════════════════════════ TEST ACCESSOR ════════════════════════════════

    /// <summary>
    /// Provides test access to chain internals.  Nested here so it can
    /// reach <c>PrivateChainNode</c> in DEBUG builds.
    /// </summary>
    internal readonly struct ChainTestAccessor
    {
        private readonly BaseProductionLoop<TPayload> _loop;

        public ChainTestAccessor(BaseProductionLoop<TPayload> loop) => _loop = loop;

        public int CurrentSequence => _loop._currentSequence;
        public ChainNode? CurrentNode => _loop._currentNode;
        public ChainNode? OldestNode => _loop._oldestNode;
        public int PinnedQueueCount => _loop._pinnedQueue.Count;
        public void SetOldestNodeForTesting(ChainNode node) => _loop._oldestNode = node;

        public ChainNode CreateNode(int sequenceNumber)
        {
            var node = _loop._nodePool.Rent();
            Mutate(node).InitializeBase(sequenceNumber);
            return node;
        }

        public void SetPayload(ChainNode node, TPayload payload) =>
            Mutate(node).SetPayload(payload);

        public void MarkPublished(ChainNode node, long timeNanoseconds) =>
            Mutate(node).MarkPublished(timeNanoseconds);

        public void LinkNodes(ChainNode current, ChainNode next) =>
            Mutate(current).SetNext(next);

        public void ClearNext(ChainNode node) => Mutate(node).ClearNext();

        public void ClearPayload(ChainNode node) => Mutate(node).ClearPayload();

        public void AddRef(ChainNode node) => Mutate(node).AddRef();

        public void Release(ChainNode node) => Mutate(node).Release();
    }

    internal ChainTestAccessor GetChainTestAccessor() => new(this);
}
