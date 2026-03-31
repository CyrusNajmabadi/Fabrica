namespace Engine.Pipeline;

internal abstract partial class BaseProductionLoop<TPayload>
{
    /// <summary>
    /// Provides test access to chain internals.  Nested here so it can
    /// reach <c>PrivateChainNode</c> in DEBUG builds.
    /// </summary>
    public readonly struct ChainTestAccessor
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

        public ChainNode? GetNext(ChainNode node) => Mutate(node).GetNext();

        public void LinkNodes(ChainNode current, ChainNode next) =>
            Mutate(current).SetNext(next);

        public void ClearNext(ChainNode node) => Mutate(node).ClearNext();

        public void ClearPayload(ChainNode node) => Mutate(node).ClearPayload();

        public void AddRef(ChainNode node) => Mutate(node).AddRef();

        public void Release(ChainNode node) => Mutate(node).Release();
    }

    public ChainTestAccessor GetChainTestAccessor() => new(this);
}
