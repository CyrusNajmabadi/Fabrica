namespace Fabrica.Core;

/// <summary>
/// Exposes compile-time build configuration flags at runtime,
/// so benchmarks and diagnostics can verify which optimizations were active.
/// </summary>
public static class BuildConfig
{
    public static bool UnsafeOptimizations =>
#if UNSAFE_OPT
        true;
#else
        false;
#endif
}
