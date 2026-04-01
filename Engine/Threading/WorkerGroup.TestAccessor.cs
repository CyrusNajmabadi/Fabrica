namespace Engine.Threading;

internal sealed partial class WorkerGroup<TState, TExecutor>
{
    public TestAccessor GetTestAccessor() => new(this);

    public readonly struct TestAccessor(WorkerGroup<TState, TExecutor> group)
    {
        private readonly WorkerGroup<TState, TExecutor> _group = group;

        /// <summary>
        /// Waits for all worker threads to exit within the given timeout. Returns true if all threads exited, false if the
        /// timeout elapsed.
        /// </summary>
        public bool Join(int millisecondsTimeout)
        {
            foreach (var worker in _group._workers)
            {
                if (!worker.Join(millisecondsTimeout))
                    return false;
            }

            return true;
        }
    }
}
