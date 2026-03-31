using Engine.Memory;

namespace Engine.Pipeline;

internal abstract partial class BaseProductionLoop<TPayload>
{
    /// <summary>
    /// Allocator for the node pool.  Nested here so it can construct
    /// <c>PrivateChainNode</c> in DEBUG builds (the type is private to this class).
    /// </summary>
    internal struct ChainNodeAllocator : IAllocator<ChainNode>
    {
        public ChainNode Allocate() =>
#if DEBUG
            new PrivateChainNode();
#else
            new ChainNode();
#endif

        public void Reset(ChainNode item) { }
    }
}
