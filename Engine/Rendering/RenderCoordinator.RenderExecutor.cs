using Engine.Threading;

namespace Engine.Rendering;

internal sealed partial class RenderCoordinator
{
    /// <summary>
    /// Domain-specific executor for render frame work.  Each render worker
    /// thread owns one instance, which holds the worker's private
    /// <see cref="RenderWorkerResources"/>.
    ///
    /// <see cref="Prepare"/> clears per-frame state before each dispatch.
    /// <see cref="Execute"/> performs the worker's portion of the frame rendering.
    /// </summary>
    public readonly struct RenderExecutor : IThreadExecutor<RenderDispatchState>
    {
        public readonly RenderWorkerResources Resources;

        public RenderExecutor(RenderWorkerResources resources) =>
            Resources = resources;

        public void Prepare() =>
            Resources.PrepareForFrame();

        public void Execute(in RenderDispatchState state, CancellationToken cancellationToken)
        {
            // TODO: actual per-worker render computation.
            // Read state.Frame.Latest and state.Frame.Previous (immutable snapshots),
            // compute this worker's portion of the frame output.
            // Check cancellationToken for early exit during engine shutdown.
        }
    }
}
