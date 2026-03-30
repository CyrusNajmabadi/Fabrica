using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// Dispatches save work to run asynchronously (off the consumption thread).
/// Implementations are constrained to struct for zero interface-dispatch overhead.
///
/// The runner receives the image, the tick number, and a callback (<paramref
/// name="saveAction"/>) to invoke when the work runs.  The callback encapsulates
/// the unpin and reschedule logic so the runner implementation only needs to
/// arrange execution (e.g. Task.Run) without knowing save internals.
///
/// Tests substitute a synchronous, controllable runner so that save dispatch and
/// completion can be driven step-by-step without real threads or timing.
/// </summary>
internal interface ISaveRunner
{
    void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction);
}
