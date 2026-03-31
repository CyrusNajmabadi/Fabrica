namespace Engine.Pipeline;

internal sealed partial class ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter>
{
    public TestAccessor GetTestAccessor() => new(this);

    public readonly struct TestAccessor
    {
        private readonly ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter> _loop;

        public TestAccessor(ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter> loop) => _loop = loop;

        public void RunOneIteration(CancellationToken cancellationToken) => _loop.RunOneIteration(cancellationToken);

        public void ThrottleToFrameRate(long frameStart, CancellationToken cancellationToken) =>
            _loop.ThrottleToFrameRate(frameStart, cancellationToken);
    }
}
