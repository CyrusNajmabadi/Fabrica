namespace Fabrica.Core.Threading;

/// <summary>
/// Native interop for thread-to-core pinning and QoS. Declared at namespace scope because
/// <c>[LibraryImport]</c> cannot be applied inside generic types (CS7042).
///
/// PLATFORM BEHAVIOUR
///
///   Windows / Linux: hard-pins the calling thread to a specific logical core via
///   <c>SetThreadAffinityMask</c> / <c>sched_setaffinity</c>.
///
///   macOS: hard-pinning is not available (the XNU kernel rejects <c>THREAD_AFFINITY_POLICY</c>
///   on Apple Silicon). Instead, <see cref="TrySetHighQos"/> sets the thread's QoS class to
///   <c>QOS_CLASS_USER_INITIATED</c> via <c>pthread_set_qos_class_self_np</c>. This tells the OS
///   scheduler to prefer performance cores over efficiency cores — the right behaviour for
///   latency-sensitive worker threads in a work-stealing pool.
///
///   IMPORTANT — .NET THREAD LIMITATION (macOS):
///   Threads created via <c>new Thread(...)</c> in .NET start with <c>QOS_CLASS_UNSPECIFIED</c>
///   and the kernel rejects <c>pthread_set_qos_class_self_np</c> (EPERM) on such threads. To work
///   around this, use <see cref="StartNativeThreadWithHighQos"/> which creates a real pthread with
///   <c>QOS_CLASS_USER_INITIATED</c> set at creation time via <c>pthread_attr_set_qos_class_np</c>.
///   When the thread calls into managed code via its delegate, .NET attaches it as a managed thread
///   while preserving the native QoS.
/// </summary>
public static partial class ThreadPinningNative
{
    /// <summary>
    /// Attempts to pin the calling thread to the specified logical core. Returns true if the OS
    /// accepted the request, false otherwise. On macOS this is a no-op (returns false) — use
    /// <see cref="TrySetHighQos"/> instead to steer threads onto performance cores.
    /// </summary>
    public static bool TryPinCurrentThread(int coreIndex)
    {
        if ((uint)coreIndex >= (uint)Environment.ProcessorCount)
            return false;

        try
        {
            if (OperatingSystem.IsWindows())
                return TryPinWindows(coreIndex);
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Sets the calling thread to a high QoS class so the OS scheduler prefers performance cores.
    /// On macOS, sets <c>QOS_CLASS_USER_INITIATED</c> (0x19). On other platforms, this is a no-op.
    /// NOTE: This will fail (return false) on .NET-managed threads on macOS because they are created
    /// with <c>QOS_CLASS_UNSPECIFIED</c>. Use <see cref="StartNativeThreadWithHighQos"/> to create
    /// threads that support QoS.
    /// </summary>
    public static bool TrySetHighQos()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return PthreadSetQosClassSelfNp(QOS_CLASS_USER_INITIATED, 0) == 0;
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Starts a native thread with <c>QOS_CLASS_USER_INITIATED</c> pre-set in the pthread attributes.
    /// On macOS this bypasses the .NET limitation where <c>pthread_set_qos_class_self_np</c> fails on
    /// managed threads. On other platforms, falls back to <c>new Thread(...)</c> with pinning.
    /// The callback is invoked on the new thread; it must keep the thread alive (e.g. spin in a loop).
    /// </summary>
    public static Thread StartNativeThreadWithHighQos(
        string name, Action callback, int coreIndex = -1, bool isBackground = true)
    {
        if (OperatingSystem.IsMacOS())
        {
            return StartMacOSNativeThread(name, callback, isBackground);
        }

        var thread = new Thread(() =>
        {
            if (coreIndex >= 0)
                TryPinCurrentThread(coreIndex);
            callback();
        })
        {
            Name = name,
            IsBackground = isBackground
        };
        thread.Start();
        return thread;
    }
}
