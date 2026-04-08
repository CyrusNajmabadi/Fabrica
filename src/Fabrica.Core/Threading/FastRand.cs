// Adapted from Tokio (https://github.com/tokio-rs/tokio), MIT License.
// Copyright (c) Tokio Contributors.

namespace Fabrica.Core.Threading;

/// <summary>
/// Lock-free, per-thread fast pseudo-random number generator using <c>xorshift128+</c>.
/// Two 32-bit xorshift sequences added together with shift triplet [17, 7, 16] from Marsaglia's
/// paper (<see href="https://www.jstatsoft.org/article/view/v008i14/xorshift.pdf"/>). Passes the
/// SmallCrush suite from TestU01.
///
/// <para>
/// Adapted from Tokio's <c>FastRand</c>
/// (<see href="https://github.com/tokio-rs/tokio/blob/master/tokio/src/util/rand.rs"/>).
/// </para>
///
/// <para>
/// This is NOT cryptographically secure. It is designed for low-overhead randomization of
/// work-stealing target selection, where the only requirement is spatial decorrelation across
/// workers to avoid contention on the same victim deque.
/// </para>
/// </summary>
internal struct FastRand
{
    private uint _s0;
    private uint _s1;

    /// <summary>
    /// Creates a new generator from a 64-bit seed. The seed is split into two 32-bit halves;
    /// the low half is forced non-zero (the algorithm degenerates to all-zeros if both state
    /// words are zero).
    /// </summary>
    internal FastRand(ulong seed)
    {
        _s0 = (uint)(seed >> 32);
        var low = (uint)seed;
        _s1 = low == 0 ? 1u : low;
    }

    /// <summary>
    /// Returns a uniformly distributed value in <c>[0, n)</c> without using modulo.
    /// Uses Lemire's fast range reduction
    /// (<see href="https://lemire.me/blog/2016/06/27/a-fast-alternative-to-the-modulo-reduction/"/>).
    /// </summary>
    internal uint NextN(uint n)
    {
        var r = this.Next();
        return (uint)(((ulong)r * n) >> 32);
    }

    /// <summary>Returns the next raw 32-bit pseudo-random value.</summary>
    internal uint Next()
    {
        var s1 = _s0;
        var s0 = _s1;

        s1 ^= s1 << 17;
        s1 = s1 ^ s0 ^ (s1 >> 7) ^ (s0 >> 16);

        _s0 = s0;
        _s1 = s1;

        return s0 + s1;
    }
}
