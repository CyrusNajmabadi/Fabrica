namespace Fabrica.Pipeline;

public sealed partial class ProductionLoop<TPayload, TProducer, TClock, TWaiter>
{
    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor(ProductionLoop<TPayload, TProducer, TClock, TWaiter> loop)
    {
        private readonly ProductionLoop<TPayload, TProducer, TClock, TWaiter> _loop = loop;

        public void Bootstrap() => _loop.Bootstrap(CancellationToken.None);

        public void Tick() => _loop.Tick(CancellationToken.None);

        public void Cleanup() => _loop.Cleanup();

        public void RunOneIteration(
            CancellationToken cancellationToken,
            ref long lastTime,
            ref long accumulator) =>
            _loop.RunOneIteration(cancellationToken, ref lastTime, ref accumulator);

        public TPayload CurrentPayload => _loop._currentPayload;

        public int PinnedPayloadCount => _loop._pinnedPayloads.Count;
    }
}
