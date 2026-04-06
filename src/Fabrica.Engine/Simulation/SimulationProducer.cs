using Fabrica.Core.Jobs;
using Fabrica.Core.Memory;
using Fabrica.Engine.World;
using Fabrica.Pipeline;

namespace Fabrica.Engine.Simulation;

/// <summary>
/// Adapts the simulation-specific tick logic to the generic <see cref="IProducer{TPayload}"/> interface.
///
/// Owns the image pool (allocation is a producer concern) and references the shared <see cref="WorkerPool"/>
/// and per-pipeline <see cref="JobScheduler"/> that drive parallel tick work. The pool's allocator handles
/// <see cref="WorldImage.ResetForPool"/> on return, so <see cref="ReleaseResources"/> just returns to the pool.
///
/// TICK FLOW (once WorldImage has real node types)
///   1. Rent a fresh <see cref="WorldImage"/> from the pool.
///   2. Reset per-worker <see cref="ThreadLocalBuffer{T}"/> instances.
///   3. Build the job DAG for this tick and call <see cref="JobScheduler.Submit"/> (blocks until complete).
///   4. Run <see cref="MergePipeline"/> phases to drain TLBs into global arenas with correct refcounts.
///   5. Build <see cref="SnapshotSlice{TNode,TNodeOps}"/> instances from collected roots.
///   6. Return the populated image.
/// </summary>
internal readonly struct SimulationProducer(
    ObjectPool<WorldImage, WorldImage.Allocator> imagePool,
    WorkerPool workerPool,
    JobScheduler scheduler) : IProducer<WorldImage>
{
    private readonly ObjectPool<WorldImage, WorldImage.Allocator> _imagePool = imagePool;

    // Stored for future use when WorldImage gains real node types and the tick
    // builds a job DAG. Suppressing IDE0052 since these are intentionally forward-declared.
#pragma warning disable IDE0052
    private readonly WorkerPool _workerPool = workerPool;
    private readonly JobScheduler _scheduler = scheduler;
#pragma warning restore IDE0052

    public WorldImage CreateInitialPayload(CancellationToken cancellationToken)
        => _imagePool.Rent();

    public WorldImage Produce(WorldImage current, CancellationToken cancellationToken)
    {
        var image = _imagePool.Rent();

        // TODO: Once WorldImage contains real node types, this method will:
        //   1. Reset per-worker TLBs for each node type.
        //   2. Build the tick's job DAG (concrete Job subclasses that allocate nodes in TLBs).
        //   3. _scheduler.Submit(rootJob) — blocks until the entire DAG completes.
        //   4. MergePipeline.DrainBuffers / RewriteHandles / IncrementChildRefCounts per type.
        //   5. MergePipeline.CollectAndRemapRoots + store.IncrementRoots per type.
        //   6. Populate SnapshotSlices on the WorldImage.
        //
        // The integration test in JobMergePipelineTests proves this pipeline end-to-end
        // with concrete node types and real worker threads.

        return image;
    }

    public void ReleaseResources(WorldImage payload)
        => _imagePool.Return(payload);
}
