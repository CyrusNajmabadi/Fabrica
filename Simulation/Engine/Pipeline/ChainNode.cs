using System.Diagnostics;

namespace Simulation.Engine;

/// <summary>
/// A node in a forward-linked chain managed by a producer/consumer pipeline.
/// Holds a <typeparamref name="TPayload"/> alongside chain mechanics: forward
/// pointer, sequence number, publish timestamp, ref-counting, and a bounded
/// struct iterator.
///
/// The producer creates payloads; the loop wraps them in chain nodes.  Only the
/// production loop creates, links, and frees nodes — the payload is the only
/// domain-specific concept.
///
/// CHAIN STRUCTURE
///   The producer thread builds a singly-linked forward chain, oldest → newest:
///
///     [seq 0] → [seq 1] → [seq 2] → … → [seq N]
///      _oldest                             _current / LatestNode
///
///   Only the producer thread reads or writes the chain.  The forward pointer
///   and ref-count are therefore not synchronised — no atomics, no locks needed.
///
///   The forward pointer is private.  The producer accesses it via
///   <see cref="NextInChain"/> for chain management.  Consumers use
///   <see cref="Chain"/> to obtain a safe, bounded struct iterator that stops
///   at a given endpoint — preventing accidental reads past the published frontier.
///
/// REF COUNTING
///   Each node starts with refcount = 1 (set by <see cref="InitializeBase"/>).
///   <see cref="AddRef"/> and <see cref="Release"/> support structural sharing:
///   if two consecutive nodes reference the same subtree, that subtree carries
///   refcount > 1 and must not be freed until the last node drops its reference.
///   Currently every node has refcount 1 throughout its lifetime.
///   All ref-count operations happen on the producer thread — no atomics needed.
///
///   When refcount reaches zero, the loop is responsible for clearing the
///   <see cref="Payload"/> (via <see cref="ClearPayload"/>) and releasing
///   domain resources before returning the node to the pool.
///
/// PINNED EXTRACTION AND ClearNext
///   When the producer encounters a pinned node during cleanup, it calls
///   <see cref="ClearNext"/> to sever the node from the live chain, then parks
///   it in a pinned queue.  This lets the oldest pointer advance past the pinned
///   node so that subsequent nodes can still be freed.
/// </summary>
internal sealed class ChainNode<TPayload>
{
    /// <summary>
    /// Monotonically increasing sequence number assigned by the producer.
    /// Sequence 0 is the initial state created by bootstrap.
    ///
    /// Used for epoch management, pin identification, save scheduling, and
    /// ordering within the chain.
    /// </summary>
    public int SequenceNumber { get; private set; }

    /// <summary>
    /// Wall-clock time (nanoseconds) when the producer published this node
    /// via a volatile write to the shared state.  Set on the producer thread
    /// before the release fence, so it is visible to any thread that reads
    /// the node via the matching acquire fence.
    ///
    /// Used by the consumer to compute interpolation between consecutive nodes.
    /// Lives on the node (not on the payload) because it is publication metadata.
    /// </summary>
    public long PublishTimeNanoseconds { get; private set; }

    /// <summary>
    /// The domain-specific data for this node.  Set by the production loop
    /// after renting the node from the pool; cleared by the loop before
    /// returning the node.
    /// </summary>
    public TPayload Payload { get; internal set; } = default!;

    private ChainNode<TPayload>? _next;
    private int _refCount;

    /// <summary>
    /// Internal accessor for the producer's chain management (cleanup,
    /// pinned-queue extraction).  Not for use by consumers — use
    /// <see cref="Chain"/> instead.
    /// </summary>
    internal ChainNode<TPayload>? NextInChain => _next;

    /// <summary>
    /// Prepares this node for use after being rented from the pool.
    /// Sets the sequence number, clears the forward pointer, and initialises
    /// the ref count to 1.  Called by the production loop after renting a node.
    /// </summary>
    internal void InitializeBase(int sequenceNumber)
    {
        Debug.Assert(_refCount == 0, "InitializeBase called on a node still in use");
        this.SequenceNumber = sequenceNumber;
        this.PublishTimeNanoseconds = 0;
        _next = null;
        _refCount = 1;
    }

    /// <summary>
    /// Records the wall-clock publish time.  Called by the producer thread
    /// immediately before the volatile write to the shared state, so the
    /// release/acquire pair makes the value visible to consumers.
    /// </summary>
    internal void MarkPublished(long timeNanoseconds) => this.PublishTimeNanoseconds = timeNanoseconds;

    /// <summary>Links the next node in the chain. Called once per node by the producer thread.</summary>
    internal void SetNext(ChainNode<TPayload> next)
    {
        Debug.Assert(_next is null, "SetNext called more than once on the same node");
        _next = next;
    }

    /// <summary>
    /// Severs the forward pointer when this node is extracted from the live chain
    /// into the pinned queue, so that subsequent nodes can be freed normally.
    /// </summary>
    internal void ClearNext() => _next = null;

    /// <summary>
    /// Clears the payload reference so pooled nodes do not keep domain objects alive.
    /// Called by the production loop before returning the node to the pool.
    /// </summary>
    internal void ClearPayload() => this.Payload = default!;

    internal void AddRef()
    {
        Debug.Assert(_refCount > 0, "AddRef on a zero-refcount (freed) node");
        _refCount++;
    }

    /// <summary>
    /// Decrements the ref count.  When it reaches zero, clears the forward
    /// pointer.  The caller is responsible for clearing the payload (via
    /// <see cref="ClearPayload"/>) and returning the node to the pool.
    /// </summary>
    internal void Release()
    {
        Debug.Assert(_refCount > 0, "Release called more times than AddRef");
        if (--_refCount == 0)
            _next = null;
    }

    internal bool IsUnreferenced => _refCount == 0;

    // ── Safe chain iteration ──────────────────────────────────────────────

    /// <summary>
    /// Returns a struct-based iterable over the chain from <paramref name="start"/>
    /// through <paramref name="end"/> (inclusive).  If <paramref name="start"/> is
    /// null, the chain contains only <paramref name="end"/>.
    ///
    /// The returned <see cref="ChainSegment"/> supports <c>foreach</c> with zero
    /// allocation.  It walks the private forward pointers internally, stopping at
    /// <paramref name="end"/> — the caller never touches the forward pointer and
    /// cannot accidentally read past the published frontier.
    /// </summary>
    internal static ChainSegment Chain(ChainNode<TPayload>? start, ChainNode<TPayload> end) =>
        new(start ?? end, end);

    /// <summary>
    /// A struct-based iterable over a bounded segment of the node chain.
    /// Supports <c>foreach</c> via <see cref="GetEnumerator"/> with zero allocation.
    /// </summary>
    internal readonly struct ChainSegment
    {
        private readonly ChainNode<TPayload> _start;
        private readonly ChainNode<TPayload> _end;

        internal ChainSegment(ChainNode<TPayload> start, ChainNode<TPayload> end)
        {
            _start = start;
            _end = end;
        }

        public Enumerator GetEnumerator() => new(_start, _end);

        /// <summary>
        /// Struct enumerator that walks the chain from start to end (inclusive).
        /// Accesses the private <c>_next</c> field of <see cref="ChainNode{TPayload}"/>
        /// — safe because this is a nested type of the enclosing generic class.
        /// </summary>
        public struct Enumerator
        {
            private readonly ChainNode<TPayload> _end;
            private ChainNode<TPayload>? _current;
            private bool _started;

            internal Enumerator(ChainNode<TPayload> start, ChainNode<TPayload> end)
            {
                _current = start;
                _end = end;
                _started = false;
            }

            public readonly ChainNode<TPayload> Current => _current!;

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

                _current = _current._next;
                return _current is not null;
            }
        }
    }
}
