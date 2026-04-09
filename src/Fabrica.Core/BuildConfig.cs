namespace Fabrica.Core;

public static class BuildConfig
{
    public static bool UnsafeOptimizations =>
#if UNSAFE_OPT
        true;
#else
        false;
#endif
}
