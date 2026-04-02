using Fabrica.Core.Collections;

namespace Fabrica.Pipeline;

/// <summary>
/// Consumes pipeline entries on the consumption thread. Constrained to struct for zero interface-dispatch overhead in the hot
/// path.
///
/// Called once per frame by the consumption loop when at least two entries are available (one "previous" and one or more new
/// entries). The consumer receives the full segment so it can access any entry in the range — <c>entries[0]</c> is the previous
/// entry (last entry from the prior frame, held back for interpolation) and <c>entries[entries.Count - 1]</c> is the latest.
/// </summary>
public interface IConsumer<TPayload>
{
    /// <summary>
    /// Process a frame using the available pipeline entries.
    ///
    /// <para> <paramref name="entries"/> always contains at least two items. The first entry (<c>entries[0]</c>) is the previous
    /// entry retained from the prior frame for interpolation. The last entry (<c>entries[entries.Count - 1]</c>) is the most
    /// recently published entry. Intermediate entries are available for chain iteration when the producer published multiple ticks
    /// between frames.</para>
    ///
    /// <para> <paramref name="frameStartNanoseconds"/> is the wall-clock timestamp (in nanoseconds) sampled at the beginning of
    /// the current consumption frame. It is always ≥ the latest entry's <c>PublishTimeNanoseconds</c>. The gap tells the consumer
    /// how far past the latest simulation tick real time has advanced, which is useful for interpolation.</para>
    ///
    /// <para> LIFETIME: the segment is only valid for the duration of this call. The consumption loop advances past earlier
    /// entries immediately after, so the production loop may clean up their payloads.</para>
    /// </summary>
    void Consume(
        in ProducerConsumerQueue<PipelineEntry<TPayload>>.Segment entries,
        long frameStartNanoseconds,
        CancellationToken cancellationToken);
}
