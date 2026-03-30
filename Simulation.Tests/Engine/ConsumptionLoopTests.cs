using Simulation;
using Simulation.Engine;
using Simulation.Memory;
using Simulation.Tests.Helpers;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class ConsumptionLoopTests
{
    [Fact]
    public void RunOneIteration_WithNoSnapshot_OnlyThrottles()
    {
        var test = ConsumptionLoopTestContext.Create();

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
        Assert.Empty(test.ConsumerState.ConsumedTicks);
        Assert.Empty(test.SaveRunnerState.RunCalls);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RunOneIteration_RendersSnapshot_ThenAdvancesConsumptionEpoch()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: 12);
        var epochAtConsume = -1;
        test.ConsumerState.BeforeConsume = (_, _, _) => epochAtConsume = test.Shared.ConsumptionEpoch;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([12], test.ConsumerState.ConsumedTicks);
        Assert.Equal(0, epochAtConsume);
        Assert.Equal(12, test.Shared.ConsumptionEpoch);
        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
        Assert.Same(snapshot, test.Shared.LatestNode);
    }

    [Fact]
    public void RunOneIteration_DoesNotStartSave_WhenNextSaveAtTickIsZero()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.Shared.NextSaveAtTick = 0;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Empty(test.SaveRunnerState.RunCalls);
        Assert.False(test.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(SimulationConstants.SaveIntervalTicks, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RunOneIteration_DoesNotStartSave_BeforeThresholdTick()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks - 1);

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Empty(test.SaveRunnerState.RunCalls);
        Assert.False(test.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
    }

    [Fact]
    public void RunOneIteration_StartsSave_AtThresholdTick()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);

        test.Accessor.RunOneIteration(CancellationToken.None);

        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);
        Assert.Same(snapshot, invocation.Node);
        Assert.Equal(SimulationConstants.SaveIntervalTicks, invocation.SequenceNumber);
        Assert.True(test.PinnedVersions.IsPinned(invocation.SequenceNumber));
        Assert.Equal(0, test.Shared.NextSaveAtTick);
        Assert.Equal(SimulationConstants.SaveIntervalTicks, test.Shared.ConsumptionEpoch);
        Assert.Equal([SimulationConstants.SaveIntervalTicks], test.ConsumerState.ConsumedTicks);
    }

    [Fact]
    public void RunOneIteration_StartsSave_WhenLatestTickHasAdvancedPastThreshold()
    {
        var test = ConsumptionLoopTestContext.Create();
        var tick = SimulationConstants.SaveIntervalTicks + 17;
        var snapshot = test.CreatePublishedSnapshot(tick);

        test.Accessor.RunOneIteration(CancellationToken.None);

        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);
        Assert.Same(snapshot, invocation.Node);
        Assert.Equal(tick, invocation.SequenceNumber);
        Assert.True(test.PinnedVersions.IsPinned(invocation.SequenceNumber));
        Assert.Equal(0, test.Shared.NextSaveAtTick);
        Assert.Equal(tick, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RunOneIteration_DoesNotStartSecondSave_WhileFirstIsInFlight()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);

        test.Accessor.RunOneIteration(CancellationToken.None);
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Single(test.SaveRunnerState.RunCalls);
        Assert.True(test.PinnedVersions.IsPinned(SimulationConstants.SaveIntervalTicks));
        Assert.Equal(0, test.Shared.NextSaveAtTick);
    }

    [Fact]
    public void SaveCompletion_UnpinsAndSchedulesNextSave()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);

        test.Accessor.RunOneIteration(CancellationToken.None);
        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);

        test.SaveRunnerState.Complete(invocation);

        Assert.False(test.PinnedVersions.IsPinned(invocation.SequenceNumber));
        Assert.Equal(invocation.SequenceNumber + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
        Assert.Equal([invocation.SequenceNumber], test.SaverState.SaveCalls);
    }

    [Fact]
    public void SaveFailure_StillUnpinsAndSchedulesNextSave()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.SaverState.ExceptionToThrow = new InvalidOperationException("boom");

        test.Accessor.RunOneIteration(CancellationToken.None);
        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);

        // Exception is captured in the save event queue, not propagated.
        test.SaveRunnerState.Complete(invocation);

        Assert.False(test.PinnedVersions.IsPinned(invocation.SequenceNumber));
        Assert.Equal(invocation.SequenceNumber + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
        Assert.Equal([invocation.SequenceNumber], test.SaverState.SaveCalls);

        // Failure surfaces via SaveStatus on the next consume.
        test.SaverState.ExceptionToThrow = null;
        test.Accessor.RunOneIteration(CancellationToken.None);

        var status = test.ConsumerState.ConsumedSaveStatuses[1];
        Assert.False(status.InFlight);
        Assert.NotNull(status.LastResult);
        Assert.NotNull(status.LastResult.Value.Error);
        Assert.Equal("boom", status.LastResult.Value.Error.Message);
    }

    [Fact]
    public void SaveRunnerFailure_UnpinsAndRestoresImmediateRetryState()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.SaveRunnerState.ExceptionToThrow = new InvalidOperationException("dispatch failed");

        var exception = Assert.Throws<InvalidOperationException>(
            () => test.Accessor.RunOneIteration(CancellationToken.None));

        Assert.Equal("dispatch failed", exception.Message);
        Assert.False(test.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(snapshot.TickNumber, test.Shared.NextSaveAtTick);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.Empty(test.ConsumerState.ConsumedTicks);
        Assert.Empty(test.SaverState.SaveCalls);
    }

    [Fact]
    public void ConsumerFailure_AfterSaveDispatch_LeavesEpochUnadvancedUntilSaveCompletes()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.ConsumerState.ExceptionToThrow = new InvalidOperationException("consume failed");

        var exception = Assert.Throws<InvalidOperationException>(
            () => test.Accessor.RunOneIteration(CancellationToken.None));

        Assert.Equal("consume failed", exception.Message);
        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);
        Assert.True(test.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.Equal(0, test.Shared.NextSaveAtTick);
        Assert.Empty(test.ConsumerState.ConsumedTicks);

        test.SaveRunnerState.Complete(invocation);

        Assert.False(test.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(snapshot.TickNumber + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
    }

    [Fact]
    public void ConsumerFailure_WhenNoSaveIsDue_LeavesEpochAndSaveStateUnchanged()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: 7);
        test.Shared.NextSaveAtTick = 0;
        test.ConsumerState.ExceptionToThrow = new InvalidOperationException("consume failed");

        var exception = Assert.Throws<InvalidOperationException>(
            () => test.Accessor.RunOneIteration(CancellationToken.None));

        Assert.Equal("consume failed", exception.Message);
        Assert.Empty(test.SaveRunnerState.RunCalls);
        Assert.False(test.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(0, test.Shared.NextSaveAtTick);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.Empty(test.ConsumerState.ConsumedTicks);
    }

    [Fact]
    public void ConsumerFailureAfterSaveDispatch_AllowsLaterSuccessfulConsumeOfTheSameSnapshot()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.ConsumerState.ExceptionToThrow = new InvalidOperationException("consume failed");

        Assert.Throws<InvalidOperationException>(() => test.Accessor.RunOneIteration(CancellationToken.None));
        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);

        test.SaveRunnerState.Complete(invocation);
        test.ConsumerState.ExceptionToThrow = null;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([snapshot.TickNumber], test.ConsumerState.ConsumedTicks);
        Assert.Equal(snapshot.TickNumber, test.Shared.ConsumptionEpoch);
        Assert.Equal(snapshot.TickNumber + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
        Assert.Single(test.SaveRunnerState.RunCalls);
    }

    [Fact]
    public void SaveIsPinnedBeforeDispatchAndEpochAdvancesAfterConsume()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.SaveRunnerState.BeforeDispatch = invocation =>
        {
            Assert.True(test.PinnedVersions.IsPinned(invocation.SequenceNumber));
            Assert.Equal(0, test.Shared.NextSaveAtTick);
            Assert.Equal(0, test.Shared.ConsumptionEpoch);
        };

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(SimulationConstants.SaveIntervalTicks, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RunOneIteration_ThrottlesOnlyTheRemainingFrameBudget()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: 5);
        test.ClockState.NowNanoseconds = 0;
        test.ConsumerState.BeforeConsume = (_, _, _) => test.ClockState.NowNanoseconds = 5_000_000;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal(
            [TimeSpan.FromTicks((SimulationConstants.RenderIntervalNanoseconds - 5_000_000) / 100)],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_DoesNotThrottleWhenFrameAlreadyExceededBudget()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: 5);
        test.ConsumerState.BeforeConsume =
            (_, _, _) => test.ClockState.NowNanoseconds = SimulationConstants.RenderIntervalNanoseconds + 1;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Empty(test.WaiterState.WaitCalls);
    }

    [Fact]
    public void ThrottleToFrameRate_ClampsNegativeElapsedTimeToZero()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.ClockState.NowNanoseconds = 50;

        test.Accessor.ThrottleToFrameRate(frameStart: 100, CancellationToken.None);

        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void RunOneIteration_ThrowsWhenCancelledDuringThrottleWait()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: 5);
        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(
            () => test.Accessor.RunOneIteration(cancellationSource.Token));

        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
    }

    [Fact]
    public void Run_ThrowsWhenCancelledDuringThrottleWait()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: 5);
        using var cancellationSource = new CancellationTokenSource();
        test.WaiterState.BeforeWait = cancellationSource.Cancel;

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Equal([5], test.ConsumerState.ConsumedTicks);
    }

    [Fact]
    public void Run_WhenAlreadyCancelled_DoesNothing()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: 5);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Empty(test.ConsumerState.ConsumedTicks);
        Assert.Empty(test.SaveRunnerState.RunCalls);
        Assert.Empty(test.WaiterState.WaitCalls);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RepeatedIterations_RenderTheSameLatestSnapshotUntilSimulationPublishesANewerOne()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: 5);
        test.Shared.NextSaveAtTick = 0;

        test.Accessor.RunOneIteration(CancellationToken.None);
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([5, 5], test.ConsumerState.ConsumedTicks);
        Assert.Equal(5, test.Shared.ConsumptionEpoch);
        Assert.Empty(test.SaveRunnerState.RunCalls);
    }

    [Fact]
    public void RunOneIteration_WhenSimulationPublishesMultipleTicks_ConsumerReceivesFullChain()
    {
        var test = ConsumptionLoopTestContext.Create();

        // First iteration: establish _latest as tick 1 (single snapshot, no chain).
        var tick1 = test.CreatePublishedSnapshot(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        // Simulation publishes ticks 2–6 as a chain, then sets LatestNode = tick 6.
        var chain = test.CreatePublishedChain(startTick: 2, endTick: 6);
        tick1.SetNext(chain[0]);

        WorldSnapshot? capturedPrevious = null;
        WorldSnapshot? capturedLatest = null;
        test.ConsumerState.BeforeConsume = (prev, latest, _) =>
        {
            capturedPrevious = prev;
            capturedLatest = latest;
        };

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(capturedLatest);

        // Previous should be tick 1, Latest should be tick 6.
        Assert.NotNull(capturedPrevious);
        Assert.Equal(1, capturedPrevious!.TickNumber);
        Assert.Equal(6, capturedLatest!.TickNumber);

        // Walk the chain via the struct iterator and verify every tick is present.
        var ticks = new List<int>();
        foreach (var node in ChainNode<WorldSnapshot>.Chain(capturedPrevious, capturedLatest))
            ticks.Add(node.TickNumber);

        Assert.Equal([1, 2, 3, 4, 5, 6], ticks);
    }

    [Fact]
    public void RunOneIteration_ChainIteratorStopsAtLatest_EvenWhenFurtherNodesExist()
    {
        var test = ConsumptionLoopTestContext.Create();

        var tick0 = test.CreatePublishedSnapshot(tick: 0);
        test.Accessor.RunOneIteration(CancellationToken.None);

        // Build chain: tick 1 → 2 → 3, publish tick 3 as Latest.
        // Then link a tick 4 beyond Latest to prove the iterator doesn't see it.
        var chain = test.CreatePublishedChain(startTick: 1, endTick: 3);
        tick0.SetNext(chain[0]);

        var beyondLatest = test.CreateUnpublishedSnapshot(tick: 4);
        chain[^1].SetNext(beyondLatest);

        WorldSnapshot? capturedPrevious = null;
        WorldSnapshot? capturedLatest = null;
        test.ConsumerState.BeforeConsume = (prev, latest, _) =>
        {
            capturedPrevious = prev;
            capturedLatest = latest;
        };

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(capturedLatest);
        var ticks = new List<int>();
        foreach (var node in ChainNode<WorldSnapshot>.Chain(capturedPrevious, capturedLatest!))
            ticks.Add(node.TickNumber);

        Assert.Equal([0, 1, 2, 3], ticks);
    }

    [Fact]
    public void RunOneIteration_PreviousAndLatestAreAlwaysDistinctReferences_WhenBothNonNull()
    {
        var test = ConsumptionLoopTestContext.Create();

        // Frame 1: tick 1 → Previous=null, Latest=tick1.
        test.CreatePublishedSnapshot(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        // Frame 2: same snapshot, no rotation → Previous still null, Latest still tick1.
        WorldSnapshot? previous2 = null;
        WorldSnapshot? latest2 = null;
        test.ConsumerState.BeforeConsume = (prev, latest, _) =>
        {
            previous2 = prev;
            latest2 = latest;
        };
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(latest2);
        Assert.Null(previous2);

        // Frame 3: new snapshot published → rotation happens.
        test.CreatePublishedSnapshot(tick: 2);
        WorldSnapshot? previous3 = null;
        WorldSnapshot? latest3 = null;
        test.ConsumerState.BeforeConsume = (prev, latest, _) =>
        {
            previous3 = prev;
            latest3 = latest;
        };
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(latest3);
        Assert.NotNull(previous3);
        Assert.NotSame(previous3, latest3);
        Assert.Equal(1, previous3!.TickNumber);
        Assert.Equal(2, latest3!.TickNumber);
    }

    [Fact]
    public void RunOneIteration_EpochProtectsEntireChainFromPreviousToLatest()
    {
        var test = ConsumptionLoopTestContext.Create();

        // Establish _latest as tick 1.
        test.CreatePublishedSnapshot(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        // Simulate: ticks 2–10 published as a chain.
        test.CreatePublishedChain(startTick: 2, endTick: 10);

        test.Accessor.RunOneIteration(CancellationToken.None);

        // Epoch should be set to _previous.SequenceNumber (tick 1), which keeps
        // tick 1 and all nodes through tick 10 alive (cleanup frees strictly < epoch).
        Assert.Equal(1, test.Shared.ConsumptionEpoch);
    }

    private static TimeSpan GetRenderInterval() =>
        TimeSpan.FromTicks(SimulationConstants.RenderIntervalNanoseconds / 100);

    private static class ConsumptionLoopTestContext
    {
        public static ConsumptionLoopTestContext<TestRecordingClock, TestRecordingWaiter, TestRecordingSaveRunner, TestRecordingSaver> Create()
        {
            var clockState = new TestClockState();
            var waiterState = new TestWaiterState();
            var saveRunnerState = new TestSaveRunnerState();
            var saverState = new TestSaverState();
            var consumerState = new TestConsumerState();

            return Create(
                clock: new TestRecordingClock(clockState),
                waiter: new TestRecordingWaiter(waiterState),
                saveRunner: new TestRecordingSaveRunner(saveRunnerState),
                saver: new TestRecordingSaver(saverState),
                consumerState: consumerState,
                clockState: clockState,
                waiterState: waiterState,
                saveRunnerState: saveRunnerState,
                saverState: saverState);
        }

        public static ConsumptionLoopTestContext<TClock, TWaiter, TSaveRunner, TSaver> Create<TClock, TWaiter, TSaveRunner, TSaver>(
            TClock clock,
            TWaiter waiter,
            TSaveRunner saveRunner,
            TSaver saver,
            TestConsumerState consumerState,
            TestClockState clockState,
            TestWaiterState waiterState,
            TestSaveRunnerState saveRunnerState,
            TestSaverState saverState,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
            where TSaveRunner : struct, ISaveRunner<WorldSnapshot>
            where TSaver : struct, ISaver<WorldSnapshot>
        {
            var pinnedVersions = new PinnedVersions();
            var snapshotPool = new ObjectPool<WorldSnapshot>(poolSize);
            var imagePool = new ObjectPool<WorldImage>(poolSize);
            var shared = new SharedState<WorldSnapshot>();
            var consumer = new TestRecordingConsumer(consumerState);
            var loop = new ConsumptionLoop<WorldSnapshot, TestRecordingConsumer, TSaveRunner, TSaver, TClock, TWaiter>(
                pinnedVersions,
                shared,
                consumer,
                clock,
                waiter,
                saveRunner,
                saver);
            return new ConsumptionLoopTestContext<TClock, TWaiter, TSaveRunner, TSaver>(
                pinnedVersions,
                snapshotPool,
                imagePool,
                shared,
                loop,
                consumerState,
                clockState,
                waiterState,
                saveRunnerState,
                saverState);
        }
    }

    private sealed class ConsumptionLoopTestContext<TClock, TWaiter, TSaveRunner, TSaver>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
        where TSaveRunner : struct, ISaveRunner<WorldSnapshot>
        where TSaver : struct, ISaver<WorldSnapshot>
    {
        private readonly ObjectPool<WorldSnapshot> _snapshotPool;
        private readonly ObjectPool<WorldImage> _imagePool;

        internal ConsumptionLoopTestContext(
            PinnedVersions pinnedVersions,
            ObjectPool<WorldSnapshot> snapshotPool,
            ObjectPool<WorldImage> imagePool,
            SharedState<WorldSnapshot> shared,
            ConsumptionLoop<WorldSnapshot, TestRecordingConsumer, TSaveRunner, TSaver, TClock, TWaiter> loop,
            TestConsumerState consumerState,
            TestClockState clockState,
            TestWaiterState waiterState,
            TestSaveRunnerState saveRunnerState,
            TestSaverState saverState)
        {
            this.PinnedVersions = pinnedVersions;
            _snapshotPool = snapshotPool;
            _imagePool = imagePool;
            this.Shared = shared;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
            this.ConsumerState = consumerState;
            this.ClockState = clockState;
            this.WaiterState = waiterState;
            this.SaveRunnerState = saveRunnerState;
            this.SaverState = saverState;
        }

        public PinnedVersions PinnedVersions { get; }

        public SharedState<WorldSnapshot> Shared { get; }

        public ConsumptionLoop<WorldSnapshot, TestRecordingConsumer, TSaveRunner, TSaver, TClock, TWaiter> Loop { get; }

        public ConsumptionLoop<WorldSnapshot, TestRecordingConsumer, TSaveRunner, TSaver, TClock, TWaiter>.TestAccessor Accessor { get; }

        public TestConsumerState ConsumerState { get; }

        public TestClockState ClockState { get; }

        public TestWaiterState WaiterState { get; }

        public TestSaveRunnerState SaveRunnerState { get; }

        public TestSaverState SaverState { get; }

        public WorldSnapshot CreatePublishedSnapshot(int tick)
        {
            var image = _imagePool.Rent();
            var snapshot = _snapshotPool.Rent();
            snapshot.Initialize(image, tick);
            this.Shared.LatestNode = snapshot;
            return snapshot;
        }

        public WorldSnapshot CreateUnpublishedSnapshot(int tick)
        {
            var image = _imagePool.Rent();
            var snapshot = _snapshotPool.Rent();
            snapshot.Initialize(image, tick);
            return snapshot;
        }

        /// <summary>
        /// Builds a forward-linked chain of snapshots [startTick → startTick+1 → … → endTick]
        /// and publishes the last one as LatestNode.  Returns the full array (index 0 = startTick).
        /// </summary>
        public WorldSnapshot[] CreatePublishedChain(int startTick, int endTick)
        {
            var count = endTick - startTick + 1;
            var chain = new WorldSnapshot[count];
            for (var i = 0; i < count; i++)
            {
                var image = _imagePool.Rent();
                var snapshot = _snapshotPool.Rent();
                snapshot.Initialize(image, startTick + i);
                if (i > 0)
                    chain[i - 1].SetNext(snapshot);
                chain[i] = snapshot;
            }

            this.Shared.LatestNode = chain[^1];
            return chain;
        }
    }

    private sealed class TestSaveRunnerState
    {
        public readonly List<TestSaveInvocation> RunCalls = [];

        public Action<TestSaveInvocation>? BeforeDispatch { get; set; }

        public Exception? ExceptionToThrow { get; set; }

        public void Complete(TestSaveInvocation invocation) =>
            invocation.SaveAction(invocation.Node, invocation.SequenceNumber);
    }

    private readonly struct TestRecordingSaveRunner : ISaveRunner<WorldSnapshot>
    {
        private readonly TestSaveRunnerState _state;

        public TestRecordingSaveRunner(TestSaveRunnerState state) => _state = state;

        public void RunSave(WorldSnapshot node, int sequenceNumber, Action<WorldSnapshot, int> saveAction)
        {
            var invocation = new TestSaveInvocation(node, sequenceNumber, saveAction);
            _state.RunCalls.Add(invocation);
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.BeforeDispatch?.Invoke(invocation);
        }
    }

    private sealed class TestSaverState
    {
        public readonly List<int> SaveCalls = [];

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct TestRecordingSaver : ISaver<WorldSnapshot>
    {
        private readonly TestSaverState _state;

        public TestRecordingSaver(TestSaverState state) => _state = state;

        public void Save(WorldSnapshot node, int sequenceNumber)
        {
            _state.SaveCalls.Add(sequenceNumber);
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
        }
    }

    private sealed class TestConsumerState
    {
        public readonly List<int> ConsumedTicks = [];
        public readonly List<SaveStatus> ConsumedSaveStatuses = [];
        public readonly List<(WorldSnapshot? Previous, WorldSnapshot Latest)> ConsumedPairs = [];

        public Action<WorldSnapshot?, WorldSnapshot, SaveStatus>? BeforeConsume { get; set; }

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct TestRecordingConsumer : IConsumer<WorldSnapshot>
    {
        private readonly TestConsumerState _state;

        public TestRecordingConsumer(TestConsumerState state) => _state = state;

        public void Consume(
            WorldSnapshot? previous,
            WorldSnapshot latest,
            long frameStartNanoseconds,
            SaveStatus saveStatus,
            CancellationToken cancellationToken)
        {
            _state.BeforeConsume?.Invoke(previous, latest, saveStatus);
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.ConsumedTicks.Add(latest.TickNumber);
            _state.ConsumedSaveStatuses.Add(saveStatus);
            _state.ConsumedPairs.Add((previous, latest));
        }
    }
}
