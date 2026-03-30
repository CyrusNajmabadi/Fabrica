using Simulation.Engine;
using Simulation.Memory;
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

    private static TimeSpan GetRenderInterval() =>
        TimeSpan.FromTicks(SimulationConstants.RenderIntervalNanoseconds / 100);

    private static class ConsumptionLoopTestContext
    {
        public static ConsumptionLoopTestContext<RecordingClock, RecordingWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer> Create()
        {
            var clockState = new ClockState();
            var waiterState = new WaiterState();
            var saveRunnerState = new SaveRunnerState();
            var saverState = new SaverState();
            var rendererState = new RendererState();

            return Create(
                clock: new RecordingClock(clockState),
                waiter: new RecordingWaiter(waiterState),
                saveRunner: new RecordingSaveRunner(saveRunnerState),
                saver: new RecordingSaver(saverState),
                renderer: new RecordingRenderer(rendererState),
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
            ClockState clockState,
            WaiterState waiterState,
            SaveRunnerState saveRunnerState,
            SaverState saverState,
            RendererState rendererState,
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
                renderer);
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
            ClockState clockState,
            WaiterState waiterState,
            SaveRunnerState saveRunnerState,
            SaverState saverState,
            RendererState rendererState)
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

        public ClockState ClockState { get; }

        public WaiterState WaiterState { get; }

        public SaveRunnerState SaveRunnerState { get; }

        public SaverState SaverState { get; }

        public RendererState RendererState { get; }

        public WorldSnapshot CreatePublishedSnapshot(int tick)
        {
            var image = Assert.IsType<WorldImage>(this.Memory.RentImage());
            var snapshot = Assert.IsType<WorldSnapshot>(this.Memory.RentSnapshot());
            snapshot.Initialize(image, tick);
            this.Shared.LatestSnapshot = snapshot;
            return snapshot;
        }
    }

    private sealed class ClockState
    {
        public long NowNanoseconds { get; set; }
    }

    private readonly struct RecordingClock : IClock
    {
        private readonly ClockState _state;

        public RecordingClock(ClockState state) => _state = state;

        public long NowNanoseconds => _state.NowNanoseconds;
    }

    private sealed class WaiterState
    {
        public readonly List<TimeSpan> WaitCalls = [];

        public Action? BeforeWait { get; set; }
    }

    private readonly struct RecordingWaiter : IWaiter
    {
        private readonly WaiterState _state;

        public RecordingWaiter(WaiterState state) => _state = state;

        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            _state.WaitCalls.Add(duration);
            _state.BeforeWait?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private readonly record struct SaveInvocation(WorldImage Image, int Tick, Action<WorldImage, int> SaveAction);

    private sealed class SaveRunnerState
    {
        public readonly List<SaveInvocation> RunCalls = [];

        public Action<SaveInvocation>? BeforeDispatch { get; set; }

        public Exception? ExceptionToThrow { get; set; }

        public void Complete(SaveInvocation invocation) => invocation.SaveAction(invocation.Image, invocation.Tick);
    }

    private readonly struct RecordingSaveRunner : ISaveRunner
    {
        private readonly SaveRunnerState _state;

        public RecordingSaveRunner(SaveRunnerState state) => _state = state;

        public void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction)
        {
            var invocation = new SaveInvocation(image, tick, saveAction);
            _state.RunCalls.Add(invocation);
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.BeforeDispatch?.Invoke(invocation);
        }
    }

    private sealed class SaverState
    {
        public readonly List<int> SaveCalls = [];

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct RecordingSaver : ISaver
    {
        private readonly SaverState _state;

        public RecordingSaver(SaverState state) => _state = state;

        public void Save(WorldImage image, int tick)
        {
            _state.SaveCalls.Add(tick);

            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
        }
    }

    private sealed class RendererState
    {
        public readonly List<int> RenderedTicks = [];
        public readonly List<EngineStatus> RenderedEngineStatuses = [];

        public Action<RenderFrame>? BeforeRender { get; set; }

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct RecordingRenderer : IRenderer
    {
        private readonly RendererState _state;

        public RecordingRenderer(RendererState state) => _state = state;

        public void Render(in RenderFrame frame)
        {
            _state.BeforeRender?.Invoke(frame);
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.RenderedTicks.Add(frame.Current.TickNumber);
            _state.RenderedEngineStatuses.Add(frame.EngineStatus);
        }
    }
}
