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
    public abstract class ChainNode
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
        private protected TPayload _payload = default!;
        private protected int _refCount;

#if DEBUG
        private protected ChainNode() { }
#endif

        public int SequenceNumber => _sequenceNumber;
        public long PublishTimeNanoseconds => _publishTimeNanoseconds;
        public TPayload Payload => _payload;
        internal bool IsUnreferenced => _refCount == 0;

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
