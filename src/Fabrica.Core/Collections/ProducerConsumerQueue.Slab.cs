namespace Fabrica.Core.Collections;

public sealed partial class ProducerConsumerQueue<T>
{
    /// <summary>
    /// A contiguous array segment within the queue. Slabs form a singly-linked chain owned by the producer thread. Each slab's
    /// array is sized to stay below the Large Object Heap threshold, giving cache-friendly sequential access without GC promotion
    /// pressure.
    /// </summary>
    internal sealed class Slab(int length)
    {
        /// <summary>Fixed-size array of items. Sized at construction to the slab length.</summary>
        public readonly T[] Entries = new T[length];

        /// <summary>Forward link to the next slab in the chain. Set by the producer when a new slab is appended.</summary>
        internal Slab? Next { get; set; }

        /// <summary>
        /// The global position that <c>Entries[0]</c> maps to. Set once when the slab is allocated or recycled. For example, if the
        /// slab length is 4096 and this is the third slab, <c>LogicalStartPosition</c> is 8192.
        /// </summary>
        internal long LogicalStartPosition { get; set; }
    }
}
