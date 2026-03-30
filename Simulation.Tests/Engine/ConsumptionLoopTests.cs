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
        Assert.Empty(test.RendererState.RenderedTicks);
        Assert.Empty(test.SaveRunnerState.RunCalls);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RunOneIteration_RendersSnapshot_ThenAdvancesConsumptionEpoch()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: 12);
        var epochAtRender = -1;
        test.RendererState.BeforeRender = _ => epochAtRender = test.Shared.ConsumptionEpoch;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([12], test.RendererState.RenderedTicks);
        Assert.Equal(0, epochAtRender);
        Assert.Equal(12, test.Shared.ConsumptionEpoch);
        Assert.Equal(
            [GetRenderInterval()],
            test.WaiterState.WaitCalls);
        Assert.Same(snapshot, test.Shared.LatestSnapshot);
    }

    [Fact]
    public void RunOneIteration_DoesNotStartSave_WhenNextSaveAtTickIsZero()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.Shared.NextSaveAtTick = 0;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Empty(test.SaveRunnerState.RunCalls);
        Assert.False(test.Memory.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(SimulationConstants.SaveIntervalTicks, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RunOneIteration_DoesNotStartSave_BeforeThresholdTick()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks - 1);

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Empty(test.SaveRunnerState.RunCalls);
        Assert.False(test.Memory.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
    }

    [Fact]
    public void RunOneIteration_StartsSave_AtThresholdTick()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);

        test.Accessor.RunOneIteration(CancellationToken.None);

        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);
        Assert.Same(snapshot.Image, invocation.Image);
        Assert.Equal(SimulationConstants.SaveIntervalTicks, invocation.Tick);
        Assert.True(test.Memory.PinnedVersions.IsPinned(invocation.Tick));
        Assert.Equal(0, test.Shared.NextSaveAtTick);
        Assert.Equal(SimulationConstants.SaveIntervalTicks, test.Shared.ConsumptionEpoch);
        Assert.Equal([SimulationConstants.SaveIntervalTicks], test.RendererState.RenderedTicks);
    }

    [Fact]
    public void RunOneIteration_StartsSave_WhenLatestTickHasAdvancedPastThreshold()
    {
        var test = ConsumptionLoopTestContext.Create();
        var tick = SimulationConstants.SaveIntervalTicks + 17;
        var snapshot = test.CreatePublishedSnapshot(tick);

        test.Accessor.RunOneIteration(CancellationToken.None);

        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);
        Assert.Same(snapshot.Image, invocation.Image);
        Assert.Equal(tick, invocation.Tick);
        Assert.True(test.Memory.PinnedVersions.IsPinned(invocation.Tick));
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
        Assert.True(test.Memory.PinnedVersions.IsPinned(SimulationConstants.SaveIntervalTicks));
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

        Assert.False(test.Memory.PinnedVersions.IsPinned(invocation.Tick));
        Assert.Equal(invocation.Tick + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
        Assert.Equal([invocation.Tick], test.SaverState.SaveCalls);
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

        Assert.False(test.Memory.PinnedVersions.IsPinned(invocation.Tick));
        Assert.Equal(invocation.Tick + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
        Assert.Equal([invocation.Tick], test.SaverState.SaveCalls);

        // Failure surfaces via EngineStatus on the next render frame.
        test.SaverState.ExceptionToThrow = null;
        test.Accessor.RunOneIteration(CancellationToken.None);

        var status = test.RendererState.RenderedEngineStatuses[1];
        Assert.False(status.Save.InFlight);
        Assert.NotNull(status.Save.LastResult);
        Assert.NotNull(status.Save.LastResult.Value.Error);
        Assert.Equal("boom", status.Save.LastResult.Value.Error.Message);
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
        Assert.False(test.Memory.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(snapshot.TickNumber, test.Shared.NextSaveAtTick);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.Empty(test.RendererState.RenderedTicks);
        Assert.Empty(test.SaverState.SaveCalls);
    }

    [Fact]
    public void RendererFailure_AfterSaveDispatch_LeavesEpochUnadvancedUntilSaveCompletes()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.RendererState.ExceptionToThrow = new InvalidOperationException("render failed");

        var exception = Assert.Throws<InvalidOperationException>(
            () => test.Accessor.RunOneIteration(CancellationToken.None));

        Assert.Equal("render failed", exception.Message);
        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);
        Assert.True(test.Memory.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.Equal(0, test.Shared.NextSaveAtTick);
        Assert.Empty(test.RendererState.RenderedTicks);

        test.SaveRunnerState.Complete(invocation);

        Assert.False(test.Memory.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(snapshot.TickNumber + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
    }

    [Fact]
    public void RendererFailure_WhenNoSaveIsDue_LeavesEpochAndSaveStateUnchanged()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: 7);
        test.Shared.NextSaveAtTick = 0;
        test.RendererState.ExceptionToThrow = new InvalidOperationException("render failed");

        var exception = Assert.Throws<InvalidOperationException>(
            () => test.Accessor.RunOneIteration(CancellationToken.None));

        Assert.Equal("render failed", exception.Message);
        Assert.Empty(test.SaveRunnerState.RunCalls);
        Assert.False(test.Memory.PinnedVersions.IsPinned(snapshot.TickNumber));
        Assert.Equal(0, test.Shared.NextSaveAtTick);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.Empty(test.RendererState.RenderedTicks);
    }

    [Fact]
    public void RendererFailureAfterSaveDispatch_AllowsLaterSuccessfulRenderOfTheSameSnapshot()
    {
        var test = ConsumptionLoopTestContext.Create();
        var snapshot = test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.RendererState.ExceptionToThrow = new InvalidOperationException("render failed");

        Assert.Throws<InvalidOperationException>(() => test.Accessor.RunOneIteration(CancellationToken.None));
        var invocation = Assert.Single(test.SaveRunnerState.RunCalls);

        test.SaveRunnerState.Complete(invocation);
        test.RendererState.ExceptionToThrow = null;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.Equal([snapshot.TickNumber], test.RendererState.RenderedTicks);
        Assert.Equal(snapshot.TickNumber, test.Shared.ConsumptionEpoch);
        Assert.Equal(snapshot.TickNumber + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
        Assert.Single(test.SaveRunnerState.RunCalls);
    }

    [Fact]
    public void SaveIsPinnedBeforeDispatchAndEpochAdvancesAfterRender()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: SimulationConstants.SaveIntervalTicks);
        test.SaveRunnerState.BeforeDispatch = invocation =>
        {
            Assert.True(test.Memory.PinnedVersions.IsPinned(invocation.Tick));
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
        test.RendererState.BeforeRender = _ => test.ClockState.NowNanoseconds = 5_000_000;

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
        test.RendererState.BeforeRender =
            _ => test.ClockState.NowNanoseconds = SimulationConstants.RenderIntervalNanoseconds + 1;

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

        Assert.Equal([5], test.RendererState.RenderedTicks);
    }

    [Fact]
    public void Run_WhenAlreadyCancelled_DoesNothing()
    {
        var test = ConsumptionLoopTestContext.Create();
        test.CreatePublishedSnapshot(tick: 5);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => test.Loop.Run(cancellationSource.Token));

        Assert.Empty(test.RendererState.RenderedTicks);
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

        Assert.Equal([5, 5], test.RendererState.RenderedTicks);
        Assert.Equal(5, test.Shared.ConsumptionEpoch);
        Assert.Empty(test.SaveRunnerState.RunCalls);
    }

    [Fact]
    public void RunOneIteration_WhenSimulationPublishesMultipleTicks_RendererReceivesFullChain()
    {
        var test = ConsumptionLoopTestContext.Create();

        // First iteration: establish _latest as tick 1 (single snapshot, no chain).
        var tick1 = test.CreatePublishedSnapshot(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        // Simulation publishes ticks 2–6 as a chain, then sets LatestSnapshot = tick 6.
        var chain = test.CreatePublishedChain(startTick: 2, endTick: 6);
        tick1.SetNext(chain[0]);

        RenderFrame? capturedFrame = null;
        test.RendererState.BeforeRender = frame => capturedFrame = frame;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(capturedFrame);
        var rendered = capturedFrame.Value;

        // Previous should be tick 1, Latest should be tick 6.
        Assert.NotNull(rendered.Previous);
        Assert.Equal(1, rendered.Previous!.TickNumber);
        Assert.Equal(6, rendered.Latest.TickNumber);

        // Walk the chain from Previous → Latest and verify every tick is present.
        var ticks = new List<int>();
        for (var node = rendered.Previous; node is not null; node = node.Next)
        {
            ticks.Add(node.TickNumber);
            if (ReferenceEquals(node, rendered.Latest))
                break;
        }

        Assert.Equal([1, 2, 3, 4, 5, 6], ticks);
    }

    [Fact]
    public void RunOneIteration_LatestNextIsNull_WhenNoFurtherSnapshotsAreLinked()
    {
        var test = ConsumptionLoopTestContext.Create();

        // Chain: tick 1 → 2 → 3.  Tick 3 is the tail — nothing linked after it.
        var tick0 = test.CreatePublishedSnapshot(tick: 0);
        test.Accessor.RunOneIteration(CancellationToken.None);

        var chain = test.CreatePublishedChain(startTick: 1, endTick: 3);
        tick0.SetNext(chain[0]);

        RenderFrame? capturedFrame = null;
        test.RendererState.BeforeRender = frame => capturedFrame = frame;

        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(capturedFrame);
        Assert.Null(capturedFrame.Value.Latest.Next);
    }

    [Fact]
    public void RunOneIteration_PreviousAndLatestAreAlwaysDistinctReferences_WhenBothNonNull()
    {
        var test = ConsumptionLoopTestContext.Create();

        // Frame 1: tick 1 → Previous=null, Latest=tick1.
        test.CreatePublishedSnapshot(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        // Frame 2: same snapshot, no rotation → Previous still null, Latest still tick1.
        RenderFrame? frame2 = null;
        test.RendererState.BeforeRender = frame => frame2 = frame;
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(frame2);
        Assert.Null(frame2.Value.Previous);

        // Frame 3: new snapshot published → rotation happens.
        test.CreatePublishedSnapshot(tick: 2);
        RenderFrame? frame3 = null;
        test.RendererState.BeforeRender = frame => frame3 = frame;
        test.Accessor.RunOneIteration(CancellationToken.None);

        Assert.NotNull(frame3);
        Assert.NotNull(frame3.Value.Previous);
        Assert.NotSame(frame3.Value.Previous, frame3.Value.Latest);
        Assert.Equal(1, frame3.Value.Previous!.TickNumber);
        Assert.Equal(2, frame3.Value.Latest.TickNumber);
    }

    [Fact]
    public void RunOneIteration_EpochProtectsEntireChainFromPreviousToLatest()
    {
        var test = ConsumptionLoopTestContext.Create();

        // Establish _latest as tick 1.
        test.CreatePublishedSnapshot(tick: 1);
        test.Accessor.RunOneIteration(CancellationToken.None);

        // Simulate: ticks 2–10 published as a chain.
        var chain = test.CreatePublishedChain(startTick: 2, endTick: 10);

        test.Accessor.RunOneIteration(CancellationToken.None);

        // Epoch should be set to _previous.TickNumber (tick 1), which keeps
        // tick 1 and all nodes through tick 10 alive (cleanup frees strictly < epoch).
        Assert.Equal(1, test.Shared.ConsumptionEpoch);
    }

    private static TimeSpan GetRenderInterval() =>
        TimeSpan.FromTicks(SimulationConstants.RenderIntervalNanoseconds / 100);

    private static class ConsumptionLoopTestContext
    {
        public static ConsumptionLoopTestContext<TestRecordingClock, TestRecordingWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestRecordingRenderer> Create()
        {
            var clockState = new TestClockState();
            var waiterState = new TestWaiterState();
            var saveRunnerState = new TestSaveRunnerState();
            var saverState = new TestSaverState();
            var rendererState = new TestRendererState();

            return Create(
                clock: new TestRecordingClock(clockState),
                waiter: new TestRecordingWaiter(waiterState),
                saveRunner: new TestRecordingSaveRunner(saveRunnerState),
                saver: new TestRecordingSaver(saverState),
                renderer: new TestRecordingRenderer(rendererState),
                clockState: clockState,
                waiterState: waiterState,
                saveRunnerState: saveRunnerState,
                saverState: saverState,
                rendererState: rendererState);
        }

        public static ConsumptionLoopTestContext<TClock, TWaiter, TSaveRunner, TSaver, TRenderer> Create<TClock, TWaiter, TSaveRunner, TSaver, TRenderer>(
            TClock clock,
            TWaiter waiter,
            TSaveRunner saveRunner,
            TSaver saver,
            TRenderer renderer,
            TestClockState clockState,
            TestWaiterState waiterState,
            TestSaveRunnerState saveRunnerState,
            TestSaverState saverState,
            TestRendererState rendererState,
            int poolSize = 8)
            where TClock : struct, IClock
            where TWaiter : struct, IWaiter
            where TSaveRunner : struct, ISaveRunner
            where TSaver : struct, ISaver
            where TRenderer : struct, IRenderer
        {
            var memory = new MemorySystem(poolSize);
            var shared = new SharedState();
            var loop = new ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer>(
                memory,
                shared,
                clock,
                waiter,
                saveRunner,
                saver,
                renderer,
                new RenderCoordinator(1));
            return new ConsumptionLoopTestContext<TClock, TWaiter, TSaveRunner, TSaver, TRenderer>(
                memory,
                shared,
                loop,
                clockState,
                waiterState,
                saveRunnerState,
                saverState,
                rendererState);
        }
    }

    private sealed class ConsumptionLoopTestContext<TClock, TWaiter, TSaveRunner, TSaver, TRenderer>
        where TClock : struct, IClock
        where TWaiter : struct, IWaiter
        where TSaveRunner : struct, ISaveRunner
        where TSaver : struct, ISaver
        where TRenderer : struct, IRenderer
    {
        internal ConsumptionLoopTestContext(
            MemorySystem memory,
            SharedState shared,
            ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer> loop,
            TestClockState clockState,
            TestWaiterState waiterState,
            TestSaveRunnerState saveRunnerState,
            TestSaverState saverState,
            TestRendererState rendererState)
        {
            this.Memory = memory;
            this.Shared = shared;
            this.Loop = loop;
            this.Accessor = loop.GetTestAccessor();
            this.ClockState = clockState;
            this.WaiterState = waiterState;
            this.SaveRunnerState = saveRunnerState;
            this.SaverState = saverState;
            this.RendererState = rendererState;
        }

        public MemorySystem Memory { get; }

        public SharedState Shared { get; }

        public ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer> Loop { get; }

        public ConsumptionLoop<TClock, TWaiter, TSaveRunner, TSaver, TRenderer>.TestAccessor Accessor { get; }

        public TestClockState ClockState { get; }

        public TestWaiterState WaiterState { get; }

        public TestSaveRunnerState SaveRunnerState { get; }

        public TestSaverState SaverState { get; }

        public TestRendererState RendererState { get; }

        public WorldSnapshot CreatePublishedSnapshot(int tick)
        {
            var image = Assert.IsType<WorldImage>(this.Memory.RentImage());
            var snapshot = Assert.IsType<WorldSnapshot>(this.Memory.RentSnapshot());
            snapshot.Initialize(image, tick);
            this.Shared.LatestSnapshot = snapshot;
            return snapshot;
        }

        /// <summary>
        /// Builds a forward-linked chain of snapshots [startTick → startTick+1 → … → endTick]
        /// and publishes the last one as LatestSnapshot.  Returns the full array (index 0 = startTick).
        /// </summary>
        public WorldSnapshot[] CreatePublishedChain(int startTick, int endTick)
        {
            var count = endTick - startTick + 1;
            var chain = new WorldSnapshot[count];
            for (var i = 0; i < count; i++)
            {
                var image = Assert.IsType<WorldImage>(this.Memory.RentImage());
                var snapshot = Assert.IsType<WorldSnapshot>(this.Memory.RentSnapshot());
                snapshot.Initialize(image, startTick + i);
                if (i > 0)
                    chain[i - 1].SetNext(snapshot);
                chain[i] = snapshot;
            }

            this.Shared.LatestSnapshot = chain[^1];
            return chain;
        }
    }

    private sealed class TestSaveRunnerState
    {
        public readonly List<TestSaveInvocation> RunCalls = [];

        public Action<TestSaveInvocation>? BeforeDispatch { get; set; }

        public Exception? ExceptionToThrow { get; set; }

        public void Complete(TestSaveInvocation invocation) => invocation.SaveAction(invocation.Image, invocation.Tick);
    }

    private readonly struct TestRecordingSaveRunner : ISaveRunner
    {
        private readonly TestSaveRunnerState _state;

        public TestRecordingSaveRunner(TestSaveRunnerState state) => _state = state;

        public void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction)
        {
            var invocation = new TestSaveInvocation(image, tick, saveAction);
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

    private readonly struct TestRecordingSaver : ISaver
    {
        private readonly TestSaverState _state;

        public TestRecordingSaver(TestSaverState state) => _state = state;

        public void Save(WorldImage image, int tick)
        {
            _state.SaveCalls.Add(tick);

            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
        }
    }

    private sealed class TestRendererState
    {
        public readonly List<int> RenderedTicks = [];
        public readonly List<EngineStatus> RenderedEngineStatuses = [];

        public Action<RenderFrame>? BeforeRender { get; set; }

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct TestRecordingRenderer : IRenderer
    {
        private readonly TestRendererState _state;

        public TestRecordingRenderer(TestRendererState state) => _state = state;

        public void Render(in RenderFrame frame)
        {
            _state.BeforeRender?.Invoke(frame);
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.RenderedTicks.Add(frame.Latest.TickNumber);
            _state.RenderedEngineStatuses.Add(frame.EngineStatus);
        }
    }
}
