namespace Engine.Rendering;

/// <summary>
/// Per-frame input data written by the <see cref="RenderCoordinator"/> before dispatching work to render workers. The
/// <see cref="Frame"/> contains the simulation snapshots, interpolation timing, and engine status needed to produce one frame of
/// output.
///
/// SNAPSHOT LIFETIME
///   The snapshots referenced by the frame are guaranteed alive for the duration of the dispatch — the consumption loop has not
///   yet advanced the epoch. Workers must not store references beyond their <see cref="IThreadExecutor{TState}.Execute"/> call.
/// </summary>
internal readonly struct RenderDispatchState
{
    public required RenderFrame Frame { get; init; }
}
