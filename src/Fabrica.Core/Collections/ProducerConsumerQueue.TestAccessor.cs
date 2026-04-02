namespace Fabrica.Core.Collections;

public sealed partial class ProducerConsumerQueue<T>
{
    internal readonly struct TestAccessor(ProducerConsumerQueue<T> queue)
    {
        public long ProducerPosition => Volatile.Read(ref queue._producerPosition);
        public long ConsumerPosition => Volatile.Read(ref queue._consumerPosition);
        public long CleanupPosition => queue._cleanupPosition;
        public int SlabLength => queue._slabLength;
        public Slab HeadSlab => queue._headSlab;
        public Slab TailSlab => queue._tailSlab;
        public int FreeSlabCount => queue._freeSlabs.Count;
        public bool HasFreeSlabs => queue._freeSlabs.Count > 0;
    }

    internal TestAccessor GetTestAccessor() => new(this);
}
