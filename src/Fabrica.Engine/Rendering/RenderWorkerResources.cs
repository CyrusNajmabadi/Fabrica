namespace Fabrica.Engine.Rendering;

/// <summary>
/// Per-worker resource bundle for multi-threaded rendering.
///
/// Each render worker owns a dedicated RenderWorkerResources instance, giving it exclusive access to buffers and scratch state
/// during frame rendering — no locking required.
///
/// FUTURE CONTENTS
///   As the renderer gains real work (tile rasterisation, GPU command buffers, vertex staging), this class will hold per-worker
///   scratch buffers so each worker can produce output independently.
///
/// FRAME LIFECYCLE
///   Before each frame dispatch, <see cref="PrepareForFrame"/> clears any per-frame accumulation state so each frame begins with
///   a clean slate.
/// </summary>
internal sealed class RenderWorkerResources
{
    /// <summary>
    /// Resets per-frame state in preparation for a new frame dispatch. Called by the <see cref="RenderCoordinator"/> before
    /// signaling workers.
    /// </summary>
    public void PrepareForFrame()
    {
        // Future: clear scratch buffers, reset command lists, etc.
    }
}
