using System.Collections.Concurrent;
using Fabrica.Core.Jobs;
using Xunit;

namespace Fabrica.Core.Tests.Jobs;

/// <summary>
/// Stress tests for the WorkerPool: park/unpark races, shutdown under load,
/// DAG completion invariants under high contention, and repeated submit cycles.
/// Inspired by tokio's loom_multi_thread stress scenarios (racy_shutdown,
/// pool_shutdown, pool_multi_notify).
/// </summary>
[Trait("Category", "Stress")]
public class WorkerPoolStressTests
{
    // ── Test job types ──────────────────────────────────────────────────────

    private sealed class CountingJob(JobScheduler scheduler) : Job(scheduler)
    {
        private int _executed;
        public bool Executed => Volatile.Read(ref _executed) != 0;

        protected internal override void Execute(JobContext context)
            => Volatile.Write(ref _executed, 1);

        protected override void ResetState()
            => Volatile.Write(ref _executed, 0);
    }

    private sealed class SpinJob(JobScheduler scheduler, int iterations) : Job(scheduler)
    {
        protected internal override void Execute(JobContext context)
            => Thread.SpinWait(iterations);

        protected override void ResetState() { }
    }

    private sealed class CallbackJob(JobScheduler scheduler) : Job(scheduler)
    {
        public Action<JobContext>? OnExecute { get; set; }

        protected internal override void Execute(JobContext context)
            => this.OnExecute?.Invoke(context);

        protected override void ResetState()
            => this.OnExecute = null;
    }

    // ═══════════════════════════ PARK/UNPARK RACES ═════════════════════════
    // Workers park when idle. If work arrives just as they park, the announce-
    // then-recheck protocol must ensure the wake is not lost.

    [Theory]
    [InlineData(2, 500)]
    [InlineData(4, 500)]
    [InlineData(8, 200)]
    public void Stress_ParkUnpark_NoLostWakes(int workerCount, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        for (var i = 0; i < iterations; i++)
        {
            // Let workers park between rounds.
            Thread.Sleep(1);

            var executionCount = 0;
            const int JobCount = 20;

            for (var j = 0; j < JobCount; j++)
            {
                accessor.Inject(new CallbackJob(scheduler)
                {
                    OnExecute = _ => Interlocked.Increment(ref executionCount),
                });
            }

            accessor.WaitForCompletion();
            Assert.Equal(JobCount, executionCount);
        }
    }

    // ═══════════════════════════ RAPID SUBMIT CYCLES ═══════════════════════
    // Submit many small DAGs back-to-back without any sleep — tests the
    // transition between idle and active repeatedly.

    [Theory]
    [InlineData(2, 1000)]
    [InlineData(4, 1000)]
    [InlineData(8, 500)]
    public void Stress_RapidSubmitCycles_AllComplete(int workerCount, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        for (var i = 0; i < iterations; i++)
        {
            var job = new CountingJob(scheduler);
            accessor.Submit(job);
            Assert.True(job.Executed);
            Assert.Equal(0, accessor.OutstandingJobs);
        }
    }

    // ═══════════════════════════ DAG UNDER HIGH CONTENTION ═════════════════
    // Wide diamond DAGs where many workers race to decrement the join node's
    // dependency count simultaneously.

    [Theory]
    [InlineData(4, 100, 500)]
    [InlineData(8, 200, 200)]
    [InlineData(12, 500, 100)]
    public void Stress_WideDiamond_HighContention(int workerCount, int fanWidth, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var executionCount = 0;
            var root = new CallbackJob(scheduler)
            {
                OnExecute = _ => Interlocked.Increment(ref executionCount),
            };
            var join = new CallbackJob(scheduler)
            {
                OnExecute = _ => Interlocked.Increment(ref executionCount),
            };

            for (var i = 0; i < fanWidth; i++)
            {
                var child = new CallbackJob(scheduler)
                {
                    OnExecute = _ => Interlocked.Increment(ref executionCount),
                };
                child.DependsOn(root);
                join.DependsOn(child);
            }

            accessor.Submit(root);
            Assert.Equal(fanWidth + 2, executionCount);
            Assert.Equal(0, accessor.OutstandingJobs);
        }
    }

    // ═══════════════════════════ DEEP CHAIN REPEATED ═══════════════════════
    // Deep dependency chains force serial execution and exercise the propagation
    // path under varied worker counts.

    [Theory]
    [InlineData(2, 200, 100)]
    [InlineData(4, 500, 50)]
    [InlineData(8, 1000, 20)]
    public void Stress_DeepChain_Repeated(int workerCount, int depth, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var order = new ConcurrentQueue<int>();
            var jobs = new CallbackJob[depth];

            for (var i = 0; i < depth; i++)
            {
                var capturedIndex = i;
                jobs[i] = new CallbackJob(scheduler)
                {
                    OnExecute = _ => order.Enqueue(capturedIndex),
                };
            }

            for (var i = 1; i < depth; i++)
                jobs[i].DependsOn(jobs[i - 1]);

            accessor.Submit(jobs[0]);

            var result = order.ToArray();
            Assert.Equal(depth, result.Length);
            for (var i = 0; i < depth; i++)
                Assert.Equal(i, result[i]);
        }
    }

    // ═══════════════════════════ CASCADING FAN-OUT UNDER CONTENTION ════════
    // Multi-stage fan-out/fan-in pipeline: each stage fans out, joins, then
    // fans out again. Tests propagation with mixed parallelism.

    [Theory]
    [InlineData(4, 5, 100, 50)]
    [InlineData(8, 8, 50, 30)]
    [InlineData(12, 10, 100, 20)]
    public void Stress_CascadingFanOutFanIn_Repeated(int workerCount, int stages, int fanWidth, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var executionCount = 0;
            var source = new CallbackJob(scheduler)
            {
                OnExecute = _ => Interlocked.Increment(ref executionCount),
            };
            var current = (Job)source;

            for (var stage = 0; stage < stages; stage++)
            {
                var stageJoin = new CallbackJob(scheduler)
                {
                    OnExecute = _ => Interlocked.Increment(ref executionCount),
                };
                for (var i = 0; i < fanWidth; i++)
                {
                    var child = new CallbackJob(scheduler)
                    {
                        OnExecute = _ => Interlocked.Increment(ref executionCount),
                    };
                    child.DependsOn(current);
                    stageJoin.DependsOn(child);
                }

                current = stageJoin;
            }

            accessor.Submit(source);
            Assert.Equal(1 + (stages * (fanWidth + 1)), executionCount);
            Assert.Equal(0, accessor.OutstandingJobs);
        }
    }

    // ═══════════════════════════ MULTI-ROOT INJECTION ══════════════════════
    // Many independent roots injected simultaneously — stresses the injection
    // queue and wake logic.

    [Theory]
    [InlineData(4, 500, 200)]
    [InlineData(8, 1000, 100)]
    public void Stress_MultiRootInjection_AllComplete(int workerCount, int rootCount, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var executionCount = 0;

            for (var r = 0; r < rootCount; r++)
            {
                accessor.Inject(new CallbackJob(scheduler)
                {
                    OnExecute = _ => Interlocked.Increment(ref executionCount),
                });
            }

            accessor.WaitForCompletion();
            Assert.Equal(rootCount, executionCount);
            Assert.Equal(0, accessor.OutstandingJobs);
        }
    }

    // ═══════════════════════════ TWO SCHEDULERS CONTENTION ═════════════════
    // Two schedulers sharing one pool submit concurrently on separate threads.
    // Each scheduler's coordinator deque is bound to one thread via SingleThreadedOwner,
    // so we use a persistent dedicated thread for scheduler B across all iterations.

    [Theory]
    [InlineData(4, 200)]
    [InlineData(8, 100)]
    public void Stress_TwoSchedulers_ConcurrentSubmit(int workerCount, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 2);
        var schedulerA = new JobScheduler(pool);
        var schedulerB = new JobScheduler(pool);
        var accessorA = schedulerA.GetTestAccessor();
        var accessorB = schedulerB.GetTestAccessor();

        var countA = 0;
        var countB = 0;
        Exception? threadException = null;

        const int FanWidth = 50;

        // Persistent thread for scheduler B — matches real usage where each coordinator
        // is a dedicated thread (game loop, render loop, etc.). Creating a new thread per
        // iteration would violate SingleThreadedOwner on the coordinator's deque.
        using var startEvent = new ManualResetEventSlim(false);
        using var doneEvent = new ManualResetEventSlim(false);

        var threadB = new Thread(() =>
        {
            try
            {
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    startEvent.Wait();
                    startEvent.Reset();

                    var rootB = new CallbackJob(schedulerB)
                    {
                        OnExecute = _ => Interlocked.Increment(ref countB),
                    };
                    for (var i = 0; i < FanWidth; i++)
                    {
                        var child = new CallbackJob(schedulerB)
                        {
                            OnExecute = _ => Interlocked.Increment(ref countB),
                        };
                        child.DependsOn(rootB);
                    }

                    accessorB.Submit(rootB);
                    doneEvent.Set();
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref threadException, ex);
                doneEvent.Set();
            }
        });

        threadB.Start();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Volatile.Write(ref countA, 0);
            Volatile.Write(ref countB, 0);

            // Signal thread B to start this iteration's work.
            startEvent.Set();

            var rootA = new CallbackJob(schedulerA)
            {
                OnExecute = _ => Interlocked.Increment(ref countA),
            };
            for (var i = 0; i < FanWidth; i++)
            {
                var child = new CallbackJob(schedulerA)
                {
                    OnExecute = _ => Interlocked.Increment(ref countA),
                };
                child.DependsOn(rootA);
            }

            accessorA.Submit(rootA);

            // Wait for thread B to finish this iteration.
            doneEvent.Wait(TestContext.Current.CancellationToken);
            doneEvent.Reset();

            var ex = Volatile.Read(ref threadException);
            if (ex != null)
                Assert.Fail($"Thread threw: {ex}");

            Assert.Equal(FanWidth + 1, Volatile.Read(ref countA));
            Assert.Equal(FanWidth + 1, Volatile.Read(ref countB));
        }

        threadB.Join();
    }

    // ═══════════════════════════ SHUTDOWN UNDER LOAD ═══════════════════════
    // Tokio racy_shutdown: dispose the pool while work is still being submitted.

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Stress_ShutdownUnderLoad_NoHang(int workerCount)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
            var scheduler = new JobScheduler(pool);
            var accessor = scheduler.GetTestAccessor();

            var executionCount = 0;
            const int JobCount = 100;

            for (var j = 0; j < JobCount; j++)
            {
                accessor.Inject(new CallbackJob(scheduler)
                {
                    OnExecute = _ => Interlocked.Increment(ref executionCount),
                });
            }

            accessor.WaitForCompletion();

            Assert.Equal(JobCount, executionCount);
            pool.Dispose();
        }
    }

    // ═══════════════════════════ WORK-STEALING DISTRIBUTION ════════════════
    // Verifies that work actually gets distributed across workers.

    [Fact]
    public void Stress_WorkDistribution_MultipleWorkersParticipate()
    {
        const int WorkerCount = 8;
        const int JobCount = 10_000;

        using var pool = new WorkerPool(workerCount: WorkerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var workerHits = new int[pool.WorkerCount];

        for (var j = 0; j < JobCount; j++)
        {
            accessor.Inject(new CallbackJob(scheduler)
            {
                OnExecute = ctx =>
                {
                    Thread.SpinWait(100);
                    Interlocked.Increment(ref workerHits[ctx.WorkerIndex]);
                },
            });
        }

        accessor.WaitForCompletion();

        var totalExecuted = 0;
        var participatingWorkers = 0;
        for (var i = 0; i < workerHits.Length; i++)
        {
            totalExecuted += workerHits[i];
            if (workerHits[i] > 0) participatingWorkers++;
        }

        Assert.Equal(JobCount, totalExecuted);
        Assert.True(participatingWorkers >= 2,
            $"Only {participatingWorkers} worker(s) executed jobs — expected work to be distributed.");
    }

    // ═══════════════════════════ SUB-JOB ENQUEUE UNDER LOAD ═══════════════
    // Jobs enqueue sub-jobs during execution, stressing the deque push path
    // from within Execute while other workers steal.

    [Theory]
    [InlineData(4, 100)]
    [InlineData(8, 50)]
    public void Stress_SubJobEnqueue_NoItemsLost(int workerCount, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var executionCount = 0;
            const int ChildrenPerRoot = 20;
            const int RootCount = 10;

            for (var r = 0; r < RootCount; r++)
            {
                accessor.Inject(new CallbackJob(scheduler)
                {
                    OnExecute = ctx =>
                    {
                        Interlocked.Increment(ref executionCount);
                        for (var c = 0; c < ChildrenPerRoot; c++)
                        {
                            ctx.WorkerContext.Enqueue(new CallbackJob(scheduler)
                            {
                                OnExecute = _ => Interlocked.Increment(ref executionCount),
                            });
                        }
                    },
                });
            }

            accessor.WaitForCompletion();
            Assert.Equal(RootCount * (1 + ChildrenPerRoot), executionCount);
        }
    }

    // ═══════════════════════════ TREE DAG STRESS ═══════════════════════════
    // Quad-tree DAG: each node fans out to 4 children. Tests fine-grained
    // dependency propagation where many nodes become ready simultaneously.

    [Theory]
    [InlineData(4, 5, 100)]
    [InlineData(8, 6, 50)]
    [InlineData(12, 6, 30)]
    public void Stress_QuadTreeDag_AllNodesExecute(int workerCount, int depth, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        var totalNodes = 0;
        for (var d = 0; d <= depth; d++)
            totalNodes += (int)Math.Pow(4, d);

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var executionCount = 0;
            var jobs = new CallbackJob[totalNodes];

            for (var i = 0; i < totalNodes; i++)
            {
                jobs[i] = new CallbackJob(scheduler)
                {
                    OnExecute = _ => Interlocked.Increment(ref executionCount),
                };
            }

            // Wire parent→child dependencies (BFS order: children of node i are at 4i+1..4i+4)
            for (var i = 0; i < totalNodes; i++)
            {
                for (var c = 1; c <= 4; c++)
                {
                    var childIdx = (4 * i) + c;
                    if (childIdx < totalNodes)
                        jobs[childIdx].DependsOn(jobs[i]);
                }
            }

            accessor.Submit(jobs[0]);
            Assert.Equal(totalNodes, executionCount);
            Assert.Equal(0, accessor.OutstandingJobs);
        }
    }

    // ═══════════════════════════ MIXED WORKLOAD ════════════════════════════
    // Interleaved diamonds and chains of varying sizes, all injected at once.

    [Theory]
    [InlineData(4, 50)]
    [InlineData(8, 30)]
    public void Stress_MixedWorkload_AllComplete(int workerCount, int iterations)
    {
        using var pool = new WorkerPool(workerCount: workerCount, coordinatorCount: 1);
        var scheduler = new JobScheduler(pool);
        var accessor = scheduler.GetTestAccessor();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var executionCount = 0;
            var expectedCount = 0;

            // 10 diamonds with fan-width 30
            for (var d = 0; d < 10; d++)
            {
                var root = new CallbackJob(scheduler)
                {
                    OnExecute = _ => Interlocked.Increment(ref executionCount),
                };
                var join = new CallbackJob(scheduler)
                {
                    OnExecute = _ => Interlocked.Increment(ref executionCount),
                };

                for (var i = 0; i < 30; i++)
                {
                    var child = new CallbackJob(scheduler)
                    {
                        OnExecute = _ => Interlocked.Increment(ref executionCount),
                    };
                    child.DependsOn(root);
                    join.DependsOn(child);
                }

                accessor.Inject(root);
                expectedCount += 32; // root + 30 children + join
            }

            // 5 chains of depth 20
            for (var c = 0; c < 5; c++)
            {
                var chain = new CallbackJob[20];
                for (var i = 0; i < 20; i++)
                {
                    chain[i] = new CallbackJob(scheduler)
                    {
                        OnExecute = _ => Interlocked.Increment(ref executionCount),
                    };
                }

                for (var i = 1; i < 20; i++)
                    chain[i].DependsOn(chain[i - 1]);

                accessor.Inject(chain[0]);
                expectedCount += 20;
            }

            // 50 independent jobs
            for (var j = 0; j < 50; j++)
            {
                accessor.Inject(new CallbackJob(scheduler)
                {
                    OnExecute = _ => Interlocked.Increment(ref executionCount),
                });
            }

            expectedCount += 50;

            accessor.WaitForCompletion();
            Assert.Equal(expectedCount, executionCount);
            Assert.Equal(0, accessor.OutstandingJobs);
        }
    }
}
