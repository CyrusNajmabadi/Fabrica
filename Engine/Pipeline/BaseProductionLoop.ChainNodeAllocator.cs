using Engine.Memory;

namespace Engine.Pipeline;

internal abstract partial class BaseProductionLoop<TPayload>
{
#if DEBUG
    public abstract partial class ChainNode
#else
    public partial class ChainNode
#endif
    {
        /// <summary>
        /// Allocator for the node pool.  Nested inside <see cref="ChainNode"/> so it
        /// can construct <c>PrivateChainNode</c> in DEBUG builds (the type is private
        /// to <see cref="BaseProductionLoop{TPayload}"/>).
        /// </summary>
        public struct Allocator : IAllocator<ChainNode>
        {
            public readonly ChainNode Allocate() =>
#if DEBUG
                new PrivateChainNode();
#else
                new ChainNode();
#endif

            public readonly void Reset(ChainNode item) { }
        }
    }
}
