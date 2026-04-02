namespace Fabrica.Pipeline;

/// <summary>
/// A contiguous array segment within a <see cref="SlabList{TPayload}"/>. Slabs form a singly-linked chain owned by the producer
/// thread. Each slab's array is sized by <see cref="SlabSizeHelper{TPayload}"/> to stay below the Large Object Heap threshold,
/// giving cache-friendly sequential access without GC promotion pressure.
/// </summary>
internal sealed class Slab<TPayload>(int length)
{
    public readonly PipelineEntry<TPayload>[] Entries = new PipelineEntry<TPayload>[length];
    internal Slab<TPayload>? _next;
    internal long _logicalStartPosition;
}
