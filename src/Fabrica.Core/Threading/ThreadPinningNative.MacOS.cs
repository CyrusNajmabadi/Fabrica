using System.Runtime.InteropServices;

namespace Fabrica.Core.Threading;

public static partial class ThreadPinningNative
{
    internal const uint QOS_CLASS_USER_INITIATED = 0x19;

    private static Thread StartMacOSNativeThread(string name, Action callback, bool isBackground)
    {
        Thread? managedThread = null;
        var started = new ManualResetEventSlim(false);

        NativeThreadFunc nativeEntry = _ =>
        {
            managedThread = Thread.CurrentThread;
            managedThread.Name = name;
            managedThread.IsBackground = isBackground;
            started.Set();
            callback();
        };

        var gcHandle = GCHandle.Alloc(nativeEntry);
        try
        {
            var fnPtr = Marshal.GetFunctionPointerForDelegate(nativeEntry);
            var attr = CreateHighQosPthreadAttr();
            try
            {
                var rc = PthreadCreate(out _, attr, fnPtr, nint.Zero);
                if (rc != 0)
                    throw new InvalidOperationException($"pthread_create failed: {rc}");
            }
            finally
            {
                PthreadAttrDestroy(attr);
                Marshal.FreeHGlobal(attr);
            }

            started.Wait();
        }
        catch
        {
            gcHandle.Free();
            throw;
        }

        return managedThread!;
    }

    private static nint CreateHighQosPthreadAttr()
    {
        var attr = Marshal.AllocHGlobal(128);
        PthreadAttrInit(attr);
        PthreadAttrSetQosClassNp(attr, QOS_CLASS_USER_INITIATED, 0);
        PthreadAttrSetDetachState(attr, 2); // PTHREAD_CREATE_DETACHED
        return attr;
    }

    /// <summary>
    /// Reads back the current thread's QoS class on macOS. Returns the raw <c>qos_class_t</c>
    /// value, e.g. <see cref="QOS_CLASS_USER_INITIATED"/> (0x19).
    /// </summary>
    internal static bool TryGetCurrentThreadQos(out uint qosClass)
    {
        qosClass = 0;
        try
        {
            if (OperatingSystem.IsMacOS())
                return PthreadGetQosClassNp(PthreadSelf(), out qosClass, out _) == 0;
        }
        catch
        {
        }

        return false;
    }

    // ── P/Invoke declarations ────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeThreadFunc(nint arg);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
    private static partial int PthreadSetQosClassSelfNp(uint qosClass, int relativePriority);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "pthread_get_qos_class_np")]
    private static partial int PthreadGetQosClassNp(nint thread, out uint qosClass, out int relativePriority);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "pthread_self")]
    private static partial nint PthreadSelf();

    [LibraryImport("libSystem.B.dylib", EntryPoint = "pthread_create")]
    private static partial int PthreadCreate(out nint thread, nint attr, nint startRoutine, nint arg);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "pthread_attr_init")]
    private static partial int PthreadAttrInit(nint attr);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "pthread_attr_destroy")]
    private static partial int PthreadAttrDestroy(nint attr);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "pthread_attr_set_qos_class_np")]
    private static partial int PthreadAttrSetQosClassNp(nint attr, uint qosClass, int relativePriority);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "pthread_attr_setdetachstate")]
    private static partial int PthreadAttrSetDetachState(nint attr, int detachState);
}
