using System.Diagnostics;

namespace Engine.Pipeline;

internal abstract partial class BaseProductionLoop<TPayload>
{
#if DEBUG
    private sealed class PrivateChainNode : ChainNode
#else
    partial class ChainNode
#endif
    {
        private ChainNode? _next;

        internal ChainNode? NextInChain => _next;

        internal void InitializeBase(int sequenceNumber)
        {
            Debug.Assert(_refCount == 0, "InitializeBase called on a node still in use");
            _sequenceNumber = sequenceNumber;
            _publishTimeNanoseconds = 0;
            _next = null;
            _refCount = 1;
        }

        internal void SetPayload(TPayload payload) => _payload = payload;

        internal void MarkPublished(long timeNanoseconds) => _publishTimeNanoseconds = timeNanoseconds;

        internal void SetNext(ChainNode next)
        {
            Debug.Assert(_next is null, "SetNext called more than once on the same node");
            _next = next;
        }

        internal void ClearNext() => _next = null;

        internal void ClearPayload() => _payload = default!;

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
    }
}
