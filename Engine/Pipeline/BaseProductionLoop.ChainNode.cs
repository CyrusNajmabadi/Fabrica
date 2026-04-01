namespace Engine.Pipeline;

internal abstract partial class BaseProductionLoop<TPayload>
{
#if DEBUG
    /// <summary>
    /// DEBUG: abstract base — holds all state and read-only properties directly.
    /// The private <c>PrivateChainNode</c> adds only the forward-link (<c>_next</c>)
    /// and all mutation methods.  Because <c>PrivateChainNode</c> is private, code
    /// outside <see cref="BaseProductionLoop{TPayload}"/> cannot see the mutation
    /// API or walk the chain.  Any accidental use fails to compile.
    /// </summary>
    public abstract partial class ChainNode
#else
    /// <summary>
    /// RELEASE: single class assembled from partial parts.  In release builds
    /// anyone in the assembly <em>could</em> call the internal mutation methods,
    /// but the DEBUG build (which CI also runs) will catch that at compile time
    /// because those methods do not exist on the abstract <see cref="ChainNode"/>.
    /// </summary>
    public partial class ChainNode
#endif
    {
        private protected int _sequenceNumber;
        private protected long _publishTimeNanoseconds;
#pragma warning disable IDE0370 // Suppression is required — removing default! causes CS8601
        private protected TPayload _payload = default!;
#pragma warning restore IDE0370
        private protected int _refCount;

#if DEBUG
        private protected ChainNode() { }
#endif

        /// <summary>
        /// Monotonically increasing index assigned when the node is initialized.
        /// Sequence 0 is the bootstrap node.  Each subsequent tick increments by one.
        /// The consumption thread writes <c>ConsumptionEpoch = SequenceNumber</c> to
        /// tell the production thread which nodes are safe to reclaim (all nodes with
        /// <c>SequenceNumber &lt; ConsumptionEpoch</c> may be freed).
        /// </summary>
        public int SequenceNumber => _sequenceNumber;

        /// <summary>
        /// Wall-clock timestamp (in nanoseconds) recorded by the production thread at
        /// the moment this node is published via a volatile write to
        /// <c>SharedPipelineState.LatestNode</c>.  The consumption thread uses this
        /// value together with its own wall-clock sample (<c>frameStartNanoseconds</c>)
        /// to compute how far past this tick real time has advanced, which drives
        /// interpolation between the previous and latest payloads.
        /// </summary>
        public long PublishTimeNanoseconds => _publishTimeNanoseconds;

        /// <summary>
        /// The domain-specific state for this tick (e.g. <c>WorldImage</c>).
        /// Fully immutable once the node has been published.  The production thread
        /// creates the payload, assigns it, and then publishes; after the volatile
        /// write all fields are visible to any reader without additional synchronization.
        /// </summary>
        public TPayload Payload => _payload;

        /// <summary>
        /// True when the reference count has reached zero, meaning no thread holds a
        /// live reference to this node.  The production thread checks this during
        /// cleanup to decide whether the node can be returned to the pool.
        /// </summary>
        public bool IsUnreferenced => _refCount == 0;

        public static ChainSegment Chain(ChainNode? start, ChainNode end) =>
            new(start ?? end, end);

        public readonly struct ChainSegment(ChainNode start, ChainNode end)
        {
            private readonly ChainNode _start = start;
            private readonly ChainNode _end = end;

            public Enumerator GetEnumerator() => new(_start, _end);

            public struct Enumerator(ChainNode start, ChainNode end)
            {
                private readonly ChainNode _end = end;
                private ChainNode? _current = start;
                private bool _started;

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

#if DEBUG
                    _current = ((PrivateChainNode)_current).NextInChain;
#else
                    _current = _current.NextInChain;
#endif
                    return _current is not null;
                }
            }
        }
    }
}
