namespace Fabrica.Pipeline;

/// <summary>
/// A contiguous array segment within a <see cref="SlabList{TPayload}"/>. Slabs form a singly-linked chain owned by the producer
/// thread. Each slab's array is sized to stay below the Large Object Heap threshold, giving cache-friendly sequential access
/// without GC promotion pressure.
/// </summary>
internal sealed class Slab<TPayload>(int length)
{
    /// <summary>Fixed-size array of pipeline entries. Sized at construction to the slab length.</summary>
    public readonly PipelineEntry<TPayload>[] Entries = new PipelineEntry<TPayload>[length];

    /// <summary>Forward link to the next slab in the chain. Set by the producer when a new slab is appended.</summary>
    internal Slab<TPayload>? Next { get; set; }

    /// <summary>
    /// The global position that <c>Entries[0]</c> maps to. Set once when the slab is allocated or recycled. For example, if the
    /// slab length is 4096 and this is the third slab, <c>LogicalStartPosition</c> is 8192.
    /// </summary>
    internal long LogicalStartPosition { get; set; }
}
