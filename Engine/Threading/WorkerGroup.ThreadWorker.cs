namespace Engine.Threading;

internal sealed partial class WorkerGroup<TState, TExecutor>
{
    /// <summary>
    /// A long-lived worker thread that parks between dispatches and wakes on signal
    /// to perform domain-specific work via its <typeparamref name="TExecutor"/>.
    ///
    /// THREADING MODEL
    ///   Each worker runs a dedicated background thread in a park/signal loop:
    ///
    ///     1. Thread parks on its go signal (<see cref="AutoResetEvent"/>),
    ///        consuming no CPU while idle.
    ///     2. The coordinator sets up input data (<see cref="State"/>,
    ///        <see cref="CancellationToken"/>), prepares the executor via
    ///        <see cref="IThreadExecutor{TState}.Prepare"/>, and calls
    ///        <see cref="Signal"/>.
    ///     3. The worker wakes (go signal auto-resets), calls
    ///        <see cref="IThreadExecutor{TState}.Execute"/> on its executor,
    ///        then sets its done signal.  The done signal is set in a finally
    ///        block so that cancellation or executor exceptions never leave the
    ///        coordinator's <see cref="WaitHandle.WaitAll"/> blocked.
    ///        On success the worker loops back to step 1.  On cancellation or
    ///        exception the worker exits its loop permanently — a faulted
    ///        worker must not silently accept further dispatches.
    ///     4. The coordinator waits on all done signals via
    ///        <see cref="WorkerGroup{TState,TExecutor}.WaitHandleBatch"/>.
    ///
    ///   Both signals are <see cref="AutoResetEvent"/>: the go signal auto-resets
    ///   when the worker wakes, and the done signals auto-reset when the
    ///   coordinator's <see cref="WaitHandle.WaitAll"/> returns.
    ///
    /// THREAD PINNING
    ///   At startup the worker attempts to pin itself to a specific logical core
    ///   via <see cref="ThreadPinningNative"/>.  This is best-effort — pinning may fail
    ///   silently on restricted environments or unsupported platforms.
    ///
    /// HANDLE LIFETIME
    ///   The go/done <see cref="AutoResetEvent"/> handles are not explicitly
    ///   disposed.  Their underlying <see cref="System.Runtime.InteropServices.SafeHandle"/>
    ///   instances have finalizers that release the OS handles when collected.
    ///   For a fixed-size pool of long-lived workers this is safe and avoids
    ///   propagating IDisposable up the ownership chain.
    ///
    /// GENERIC SPECIALIZATION
    ///   Both <typeparamref name="TState"/> and <typeparamref name="TExecutor"/>
    ///   are constrained to struct, so the JIT specializes each instantiation.
    ///   The executor is stored by value — no heap allocation, no vtable dispatch.
    /// </summary>
    public sealed partial class ThreadWorker
    {
        private readonly Thread _thread;
        private readonly AutoResetEvent _goSignal = new(false);
        private readonly AutoResetEvent _doneSignal = new(false);
        private readonly int _coreIndex;
        private volatile bool _shutdown;

        private TExecutor _executor;
        private TState _state;
        private CancellationToken _cancellationToken;

        public AutoResetEvent DoneEvent => _doneSignal;

        /// <summary>
        /// Provides direct mutable access to the executor so the coordinator can
        /// call <see cref="IThreadExecutor{TState}.Prepare"/> and inspect
        /// executor-owned resources (e.g. created-nodes list) after join.
        /// Returns by ref to avoid copying the struct.
        /// </summary>
        public ref TExecutor Executor => ref _executor;

        public TState State
        {
            set => _state = value;
        }

        public CancellationToken CancellationToken
        {
            set => _cancellationToken = value;
        }

        public ThreadWorker(TExecutor executor, int coreIndex, string threadName)
        {
            _executor = executor;
            _coreIndex = coreIndex;
            _thread = new Thread(this.ThreadLoop)
            {
                Name = threadName,
                IsBackground = true,
            };
            _thread.Start();
        }

        private void ThreadLoop()
        {
            ThreadPinning.TryPinCurrentThread(_coreIndex);

            while (true)
            {
                _goSignal.WaitOne();

                try
                {
                    if (_shutdown)
                        return;

                    _executor.Execute(in _state, _cancellationToken);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception)
                {
                    // TODO: store in a field and have Dispatch throw AggregateException.
                    return;
                }
                finally
                {
                    _doneSignal.Set();
                }
            }
        }

        /// <summary>
        /// Wakes the worker thread to begin its next dispatch.
        /// The go signal auto-resets when the worker wakes.
        /// </summary>
        public void Signal() =>
            _goSignal.Set();

        /// <summary>
        /// Sets the shutdown flag and unblocks the thread so it can exit.
        /// Must be followed by <see cref="Join"/> to ensure the thread has terminated.
        /// </summary>
        public void Shutdown()
        {
            _shutdown = true;
            _goSignal.Set();
        }

        /// <summary>
        /// Blocks until the worker thread has exited.
        /// </summary>
        public void Join() =>
            _thread.Join();

        /// <summary>
        /// Blocks until the worker thread has exited or the timeout elapses.
        /// </summary>
        public bool Join(int millisecondsTimeout) =>
            _thread.Join(millisecondsTimeout);
    }
}
