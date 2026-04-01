namespace Engine.Pipeline;

internal abstract partial class BaseProductionLoop<TPayload>
{
    /// <summary>
    /// Private facade that concentrates all <c>#if DEBUG</c> casts in one place. The rest of the base class calls
    /// <c>Mutate(node).Method()</c> — ifdef-free.
    /// </summary>
    private readonly struct NodeMutation(ChainNode node)
    {
#if DEBUG
        private readonly PrivateChainNode _node = (PrivateChainNode)node;
#else
        private readonly ChainNode _node = node;
#endif

        public void InitializeBase(int sequenceNumber) => _node.InitializeBase(sequenceNumber);
        public void SetPayload(TPayload payload) => _node.SetPayload(payload);
        public void MarkPublished(long time) => _node.MarkPublished(time);
        public void SetNext(ChainNode next) => _node.SetNext(next);
        public ChainNode? GetNext() => _node.NextInChain;
        public void ClearNext() => _node.ClearNext();
        public void ClearPayload() => _node.ClearPayload();
        public void AddRef() => _node.AddRef();
        public void Release() => _node.Release();
    }

    private static NodeMutation Mutate(ChainNode node) => new(node);
}
