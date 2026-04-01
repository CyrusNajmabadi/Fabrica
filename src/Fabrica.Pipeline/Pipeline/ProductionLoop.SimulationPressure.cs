namespace Fabrica.Pipeline;

public sealed partial class ProductionLoop<TPayload, TProducer, TClock, TWaiter>
{
    /// <summary>
    /// Pure pressure-delay calculations for the simulation loop. No dependencies — extracted so it can be unit-tested in
    /// isolation.
    /// </summary>
    internal static class SimulationPressure
    {
        /// <summary>
        /// Returns the nanosecond delay to insert before a tick given how far (in nanoseconds) the simulation is ahead of
        /// consumption.
        ///
        /// When the gap is at or below the low water mark, no delay — the simulation runs freely. Each additional
        /// <paramref name="bucketWidthNanoseconds"/> of gap beyond the low water mark doubles the delay (binary-exponential),
        /// capped at <paramref name="maxNanoseconds"/>:
        ///
        ///   Buckets past LWM: 0 1 2 3 4 5 6 7+ Delay: 1ms 2ms 4ms 8ms 16ms 32ms 64ms 64ms (capped)
        ///
        /// Binary-exponential was chosen because:
        ///   • It is computed with a single integer bit-shift — no floating-point.
        ///   • It responds sharply as the gap grows, giving the consumption
        ///     thread time to advance its epoch before the gap widens further.
        ///   • Delay is capped so a separate hard-ceiling loop in the caller
        ///     can take over for extreme cases.
        /// </summary>
        public static long ComputeDelay(
            long gapNanoseconds,
            long lowWaterMarkNanoseconds,
            long bucketWidthNanoseconds,
            int bucketCount,
            long baseNanoseconds,
            long maxNanoseconds)
        {
            if (gapNanoseconds <= lowWaterMarkNanoseconds)
                return 0;

            var excess = gapNanoseconds - lowWaterMarkNanoseconds;
            var bucket = (int)Math.Min((excess - 1) / bucketWidthNanoseconds, bucketCount - 1);

            var delay = baseNanoseconds << bucket;
            return Math.Min(delay, maxNanoseconds);
        }
    }
}
