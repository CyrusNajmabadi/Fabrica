using Simulation.Engine;
using Simulation.Memory;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class LoopHarnessExampleTests
{
    [Fact]
    public void SavePinnedSnapshot_SurvivesSimulationCleanup_UntilSaveCompletes()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.Shared.NextSaveAtTick = 1;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();
        WorldSnapshot tick1Snapshot = Assert.IsType<WorldSnapshot>(test.SimulationLoop.CurrentSnapshot);
        WorldImage tick1Image = tick1Snapshot.Image;

        test.ConsumptionLoop.RunIteration();
        Assert.True(test.Pins.IsPinned(1));
        Assert.Equal(1, test.ConsumptionLoop.PendingSaveCount);
        Assert.Equal(1, test.Shared.ConsumptionEpoch);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        test.ConsumptionLoop.RunIteration();
        Assert.Equal(2, test.Shared.ConsumptionEpoch);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        Assert.True(test.Pins.IsPinned(1));
        Assert.False(tick1Snapshot.IsUnreferenced);
        Assert.Equal(1, test.SimulationLoop.PinnedQueueCount);

        test.Save.CompletePendingSave();
        Assert.False(test.Pins.IsPinned(1));

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        Assert.True(tick1Snapshot.IsUnreferenced);
        Assert.Equal(0, tick1Image.TickNumber);
        Assert.Equal(0, test.SimulationLoop.PinnedQueueCount);
    }

    [Fact]
    public void SavePinAndExternalPin_BothMustClearBeforeSimulationCanReclaim()
    {
        var test = LoopHarness.Create();
        object externalOwner = new();

        test.SimulationLoop.Bootstrap();
        test.Shared.NextSaveAtTick = 1;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();
        WorldSnapshot tick1Snapshot = Assert.IsType<WorldSnapshot>(test.SimulationLoop.CurrentSnapshot);
        WorldImage tick1Image = tick1Snapshot.Image;

        test.ConsumptionLoop.RunIteration();
        test.Pins.Pin(1, externalOwner);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();
        test.ConsumptionLoop.RunIteration();

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        Assert.True(test.Pins.IsPinned(1));
        Assert.False(tick1Snapshot.IsUnreferenced);
        Assert.Equal(1, test.SimulationLoop.PinnedQueueCount);

        test.Save.CompletePendingSave();

        Assert.True(test.Pins.IsPinned(1));
        Assert.False(tick1Snapshot.IsUnreferenced);
        Assert.Equal(1, test.SimulationLoop.PinnedQueueCount);

        test.Pins.Unpin(1, externalOwner);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        Assert.False(test.Pins.IsPinned(1));
        Assert.True(tick1Snapshot.IsUnreferenced);
        Assert.Equal(0, tick1Image.TickNumber);
        Assert.Equal(0, test.SimulationLoop.PinnedQueueCount);
    }

    [Fact]
    public void ConsumptionIterationBeforeSimulationIteration_SeesThePreviouslyPublishedSnapshot()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // publish tick 1

        test.ConsumptionLoop.RunIteration(); // consume tick 1
        Assert.Equal([1], test.Renderer.RenderedTicks);
        Assert.Equal(1, test.Shared.ConsumptionEpoch);

        test.ConsumptionLoop.RunIteration(); // still consume tick 1
        Assert.Equal([1, 1], test.Renderer.RenderedTicks);
        Assert.Equal(1, test.Shared.ConsumptionEpoch);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // publish tick 2

        test.ConsumptionLoop.RunIteration(); // now see tick 2
        Assert.Equal([1, 1, 2], test.Renderer.RenderedTicks);
        Assert.Equal(2, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void UnpinnedSnapshot_IsReclaimedAfterConsumptionAdvancesPastIt()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // publish tick 1
        WorldSnapshot tick1Snapshot = Assert.IsType<WorldSnapshot>(test.SimulationLoop.CurrentSnapshot);
        WorldImage tick1Image = tick1Snapshot.Image;

        test.ConsumptionLoop.RunIteration(); // consume tick 1
        Assert.Equal(1, test.Shared.ConsumptionEpoch);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // publish tick 2
        Assert.False(tick1Snapshot.IsUnreferenced);

        test.ConsumptionLoop.RunIteration(); // consume tick 2
        Assert.Equal(2, test.Shared.ConsumptionEpoch);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // publish tick 3, reclaim tick 1

        Assert.True(tick1Snapshot.IsUnreferenced);
        Assert.Equal(0, tick1Image.TickNumber);
        Assert.Equal(0, test.SimulationLoop.PinnedQueueCount);
    }

    [Fact]
    public void SimulationIterationBeforeConsumptionIteration_PublishesNewestSnapshotForConsumption()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1
        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 2

        test.ConsumptionLoop.RunIteration();

        Assert.Equal([2], test.Renderer.RenderedTicks);
        Assert.Equal(2, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void SaveInFlight_PreventsAdditionalSaveDispatchAcrossMultipleConsumptionIterations()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.Shared.NextSaveAtTick = 1;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1

        test.ConsumptionLoop.RunIteration(); // starts save
        Assert.Equal(1, test.ConsumptionLoop.PendingSaveCount);
        Assert.True(test.Pins.IsPinned(1));

        test.ConsumptionLoop.RunIteration(); // same snapshot again
        Assert.Equal(1, test.ConsumptionLoop.PendingSaveCount);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 2
        test.ConsumptionLoop.RunIteration(); // newer snapshot, save still in flight

        Assert.Equal(1, test.ConsumptionLoop.PendingSaveCount);
        Assert.Equal([1, 1, 2], test.Renderer.RenderedTicks);
        Assert.Equal(2, test.Shared.ConsumptionEpoch);
        Assert.Equal(0, test.Shared.NextSaveAtTick);

        test.Save.CompletePendingSave();
        Assert.False(test.Pins.IsPinned(1));
        Assert.Equal(1 + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
    }

    [Fact]
    public void SaveCompletion_AllowsLaterSaveOnceScheduleIsReachedAgain()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.Shared.NextSaveAtTick = 1;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1
        test.ConsumptionLoop.RunIteration(); // start/save tick 1
        test.Save.CompletePendingSave();

        test.Shared.NextSaveAtTick = 3;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 2
        test.ConsumptionLoop.RunIteration(); // below threshold
        Assert.Equal(0, test.ConsumptionLoop.PendingSaveCount);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 3
        test.ConsumptionLoop.RunIteration(); // reaches threshold again

        Assert.Equal(1, test.ConsumptionLoop.PendingSaveCount);
        Assert.True(test.Pins.IsPinned(3));
        Assert.Equal(3, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void RenderFailureAfterSaveDispatch_AllowsLaterRecoveryOnTheSameSnapshot()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.Shared.NextSaveAtTick = 1;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1

        test.Renderer.FailWith(new InvalidOperationException("render failed"));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => test.ConsumptionLoop.RunIteration());

        Assert.Equal("render failed", exception.Message);
        Assert.Equal(1, test.Save.PendingSaveCount);
        Assert.True(test.Pins.IsPinned(1));
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.Equal(0, test.Shared.NextSaveAtTick);
        Assert.Empty(test.Renderer.RenderedTicks);

        test.Save.CompletePendingSave();

        Assert.False(test.Pins.IsPinned(1));
        Assert.Equal(1 + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);

        test.Renderer.ClearFailure();
        test.ConsumptionLoop.RunIteration();

        Assert.Equal([1], test.Renderer.RenderedTicks);
        Assert.Equal(1, test.Shared.ConsumptionEpoch);
        Assert.Equal(0, test.Save.PendingSaveCount);
    }

    [Fact]
    public void SaveRunnerDispatchFailure_AllowsLaterRetryOnTheSamePublishedSnapshot()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.Shared.NextSaveAtTick = 1;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1

        test.Save.FailDispatchWith(new InvalidOperationException("dispatch failed"));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => test.ConsumptionLoop.RunIteration());

        Assert.Equal("dispatch failed", exception.Message);
        Assert.Equal(0, test.Save.PendingSaveCount);
        Assert.False(test.Pins.IsPinned(1));
        Assert.Equal(1, test.Shared.NextSaveAtTick);
        Assert.Equal(0, test.Shared.ConsumptionEpoch);
        Assert.Empty(test.Renderer.RenderedTicks);

        test.Save.ClearDispatchFailure();
        test.ConsumptionLoop.RunIteration();

        Assert.Equal([1], test.Renderer.RenderedTicks);
        Assert.Equal(1, test.Shared.ConsumptionEpoch);
        Assert.Equal(1, test.Save.PendingSaveCount);
        Assert.True(test.Pins.IsPinned(1));
    }

    [Fact]
    public void ConsumptionCatchesUpLate_AndSavesOnlyTheNewestPublishedSnapshot()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.Shared.NextSaveAtTick = 1;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1
        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 2
        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 3

        test.ConsumptionLoop.RunIteration();

        Assert.Equal([3], test.Renderer.RenderedTicks);
        Assert.Equal(3, test.Shared.ConsumptionEpoch);
        Assert.Equal(1, test.Save.PendingSaveCount);
        Assert.True(test.Pins.IsPinned(3));
        Assert.False(test.Pins.IsPinned(1));
        Assert.False(test.Pins.IsPinned(2));
        Assert.Equal(0, test.Shared.NextSaveAtTick);

        test.Save.CompletePendingSave();

        Assert.False(test.Pins.IsPinned(3));
        Assert.Equal(3 + SimulationConstants.SaveIntervalTicks, test.Shared.NextSaveAtTick);
    }

    [Fact]
    public void NoSaveConfigured_AllowsSimulationCleanupToProceedNormally()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.Shared.NextSaveAtTick = 0;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1
        WorldSnapshot tick1Snapshot = Assert.IsType<WorldSnapshot>(test.SimulationLoop.CurrentSnapshot);
        WorldImage tick1Image = tick1Snapshot.Image;

        test.ConsumptionLoop.RunIteration(); // epoch 1, no save

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 2
        test.ConsumptionLoop.RunIteration(); // epoch 2

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 3, cleanup can reclaim tick 1

        Assert.True(tick1Snapshot.IsUnreferenced);
        Assert.Equal(0, tick1Image.TickNumber);
        Assert.Equal(0, test.ConsumptionLoop.PendingSaveCount);
    }

    private sealed class LoopHarness
    {
        private readonly SimulationLoop<RecordingClock, NoWaiter>.TestAccessor _simulationAccessor;
        private readonly ConsumptionLoop<RecordingClock, NoWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer>.TestAccessor _consumptionAccessor;
        private long _simulationLastTime;
        private long _simulationAccumulator;

        private LoopHarness(
            MemorySystem memory,
            SharedState shared,
            ClockState clockState,
            SaveRunnerState saveRunnerState,
            SaverState saverState,
            RendererState rendererState,
            SimulationLoop<RecordingClock, NoWaiter> simulationLoop,
            ConsumptionLoop<RecordingClock, NoWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer> consumptionLoop)
        {
            Memory = memory;
            Shared = shared;
            Clock = new ClockController(clockState);
            Pins = new PinController(memory.PinnedVersions);
            Save = new SaveController(saveRunnerState, saverState);
            ConsumptionLoop = new ConsumptionLoopController(consumptionLoop.GetTestAccessor(), saveRunnerState);
            SimulationLoop = new SimulationLoopController(this, simulationLoop.GetTestAccessor());
            Renderer = new RendererController(rendererState);

            _simulationAccessor = simulationLoop.GetTestAccessor();
            _consumptionAccessor = consumptionLoop.GetTestAccessor();
        }

        private MemorySystem Memory { get; }

        public SharedState Shared { get; }

        public ClockController Clock { get; }

        public PinController Pins { get; }

        public SaveController Save { get; }

        public RendererController Renderer { get; }

        public SimulationLoopController SimulationLoop { get; }

        public ConsumptionLoopController ConsumptionLoop { get; }

        public static LoopHarness Create(int poolSize = SimulationConstants.PressureBucketCount)
        {
            var memory = new MemorySystem(poolSize);
            var shared = new SharedState();
            var clockState = new ClockState();
            var saveRunnerState = new SaveRunnerState();
            var saverState = new SaverState();
            var rendererState = new RendererState();
            var clock = new RecordingClock(clockState);
            var waiter = new NoWaiter();
            var saveRunner = new RecordingSaveRunner(saveRunnerState);
            var saver = new RecordingSaver(saverState);
            var renderer = new RecordingRenderer(rendererState);
            var simulationLoop = new SimulationLoop<RecordingClock, NoWaiter>(memory, shared, clock, waiter);
            var consumptionLoop = new ConsumptionLoop<RecordingClock, NoWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer>(
                memory,
                shared,
                clock,
                waiter,
                saveRunner,
                saver,
                renderer);

            return new LoopHarness(
                memory,
                shared,
                clockState,
                saveRunnerState,
                saverState,
                rendererState,
                simulationLoop,
                consumptionLoop);
        }

        public sealed class SimulationLoopController
        {
            private readonly LoopHarness _owner;
            private readonly SimulationLoop<RecordingClock, NoWaiter>.TestAccessor _accessor;

            public SimulationLoopController(
                LoopHarness owner,
                SimulationLoop<RecordingClock, NoWaiter>.TestAccessor accessor)
            {
                _owner = owner;
                _accessor = accessor;
            }

            public WorldSnapshot? CurrentSnapshot => _accessor.CurrentSnapshot;

            public int PinnedQueueCount => _accessor.PinnedQueueCount;

            public void Bootstrap()
            {
                _accessor.Bootstrap();
                _owner._simulationLastTime = _owner.Clock.NowNanoseconds;
                _owner._simulationAccumulator = 0;
            }

            public void RunIteration()
            {
                _accessor.RunOneIteration(
                    CancellationToken.None,
                    ref _owner._simulationLastTime,
                    ref _owner._simulationAccumulator);
            }

        }

        public sealed class ConsumptionLoopController
        {
            private readonly ConsumptionLoop<RecordingClock, NoWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer>.TestAccessor _accessor;
            private readonly SaveRunnerState _saveRunnerState;

            public ConsumptionLoopController(
                ConsumptionLoop<RecordingClock, NoWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer>.TestAccessor accessor,
                SaveRunnerState saveRunnerState)
            {
                _accessor = accessor;
                _saveRunnerState = saveRunnerState;
            }

            public int PendingSaveCount => _saveRunnerState.PendingInvocations.Count;

            public void RunIteration() => _accessor.RunOneIteration(CancellationToken.None);
        }

        public sealed class ClockController
        {
            private readonly ClockState _state;

            public ClockController(ClockState state)
            {
                _state = state;
            }

            public long NowNanoseconds => _state.NowNanoseconds;

            public void AdvanceBy(long nanoseconds) => _state.NowNanoseconds += nanoseconds;
        }

        public sealed class PinController
        {
            private readonly PinnedVersions _pins;

            public PinController(PinnedVersions pins)
            {
                _pins = pins;
            }

            public void Pin(int tick, object owner) => _pins.Pin(tick, owner);

            public void Unpin(int tick, object owner) => _pins.Unpin(tick, owner);

            public bool IsPinned(int tick) => _pins.IsPinned(tick);
        }

        public sealed class SaveController
        {
            private readonly SaveRunnerState _state;
            private readonly SaverState _saverState;

            public SaveController(SaveRunnerState state, SaverState saverState)
            {
                _state = state;
                _saverState = saverState;
            }

            public int PendingSaveCount => _state.PendingInvocations.Count;

            public IReadOnlyList<(int imageTick, int tick)> SaveCalls => _saverState.SaveCalls;

            public void FailDispatchWith(Exception exception) => _state.ExceptionToThrow = exception;

            public void ClearDispatchFailure() => _state.ExceptionToThrow = null;

            public void CompletePendingSave()
            {
                SaveInvocation invocation = Assert.Single(_state.PendingInvocations);
                _state.PendingInvocations.Clear();
                _state.Complete(invocation);
            }
        }

        public sealed class RendererController
        {
            private readonly RendererState _state;

            public RendererController(RendererState state)
            {
                _state = state;
            }

            public IReadOnlyList<int> RenderedTicks => _state.RenderedTicks;

            public void FailWith(Exception exception) => _state.ExceptionToThrow = exception;

            public void ClearFailure() => _state.ExceptionToThrow = null;
        }
    }

    private sealed class ClockState
    {
        public long NowNanoseconds { get; set; }
    }

    private readonly struct RecordingClock : IClock
    {
        private readonly ClockState _state;

        public RecordingClock(ClockState state)
        {
            _state = state;
        }

        public long NowNanoseconds => _state.NowNanoseconds;
    }

    private readonly struct NoWaiter : IWaiter
    {
        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private readonly record struct SaveInvocation(WorldImage Image, int Tick, Action<WorldImage, int> SaveAction);

    private sealed class SaveRunnerState
    {
        public readonly List<SaveInvocation> DispatchHistory = [];
        public readonly List<SaveInvocation> PendingInvocations = [];

        public Exception? ExceptionToThrow { get; set; }

        public void Complete(SaveInvocation invocation)
        {
            invocation.SaveAction(invocation.Image, invocation.Tick);
        }
    }

    private readonly struct RecordingSaveRunner : ISaveRunner
    {
        private readonly SaveRunnerState _state;

        public RecordingSaveRunner(SaveRunnerState state)
        {
            _state = state;
        }

        public void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction)
        {
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;

            var invocation = new SaveInvocation(image, tick, saveAction);
            _state.DispatchHistory.Add(invocation);
            _state.PendingInvocations.Add(invocation);
        }
    }

    private sealed class SaverState
    {
        public readonly List<(int imageTick, int tick)> SaveCalls = [];
    }

    private readonly struct RecordingSaver : ISaver
    {
        private readonly SaverState _state;

        public RecordingSaver(SaverState state)
        {
            _state = state;
        }

        public void Save(WorldImage image, int tick)
        {
            _state.SaveCalls.Add((image.TickNumber, tick));
        }
    }

    private sealed class RendererState
    {
        public readonly List<int> RenderedTicks = [];

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct RecordingRenderer : IRenderer
    {
        private readonly RendererState _state;

        public RecordingRenderer(RendererState state)
        {
            _state = state;
        }

        public void Render(in RenderFrame frame)
        {
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.RenderedTicks.Add(frame.Current.Image.TickNumber);
        }
    }
}
