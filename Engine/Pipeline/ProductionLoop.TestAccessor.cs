namespace Engine.Pipeline;

internal sealed partial class ProductionLoop<TPayload, TProducer, TClock, TWaiter>
{
    public TestAccessor GetTestAccessor() => new(this);

    public readonly struct TestAccessor(ProductionLoop<TPayload, TProducer, TClock, TWaiter> loop)
    {
        private readonly ProductionLoop<TPayload, TProducer, TClock, TWaiter> _loop = loop;
        private readonly ChainTestAccessor _chain = loop.GetChainTestAccessor();

        public void Bootstrap() => _loop.Bootstrap(CancellationToken.None);

        public void Tick() => _loop.Tick(CancellationToken.None);

        public void CleanupStaleNodes() => _loop.CleanupStaleNodes(_loop._shared.ConsumptionEpoch);

        public void RunOneIteration(
            CancellationToken cancellationToken,
            ref long lastTime,
            ref long accumulator) =>
            _loop.RunOneIteration(cancellationToken, ref lastTime, ref accumulator);

        public int CurrentSequence => _chain.CurrentSequence;

        public ChainNode? CurrentNode => _chain.CurrentNode;

        public ChainNode? OldestNode => _chain.OldestNode;

        public int PinnedQueueCount => _chain.PinnedQueueCount;

        public ChainNode? GetNext(ChainNode node) => _chain.GetNext(node);

        public void SetOldestNodeForTesting(ChainNode node) => _chain.SetOldestNodeForTesting(node);
    }
}
