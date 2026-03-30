using Simulation.Engine;
using Simulation.Memory;
using Simulation.Tests.Helpers;
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
        test.SimulationLoop.RunIteration(); // T1
        var tick1Snapshot = Assert.IsType<WorldSnapshot>(test.SimulationLoop.CurrentSnapshot);

        test.ConsumptionLoop.RunIteration(); // save dispatched for T1
        Assert.True(test.Pins.IsPinned(1));
        Assert.Equal(1, test.ConsumptionLoop.PendingSaveCount);
        Assert.Equal(1, test.Shared.ConsumptionEpoch);

        // Advance several ticks while save is in flight.  The epoch eventually
        // passes T1, but the pin keeps it alive in the simulation's pinned queue.
        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.True(test.Pins.IsPinned(1));
        Assert.False(tick1Snapshot.IsUnreferenced);

        test.Save.CompletePendingSave();
        Assert.False(test.Pins.IsPinned(1));

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        Assert.True(tick1Snapshot.IsUnreferenced);
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
        test.SimulationLoop.RunIteration(); // T1
        var tick1Snapshot = Assert.IsType<WorldSnapshot>(test.SimulationLoop.CurrentSnapshot);

        test.ConsumptionLoop.RunIteration(); // save dispatched for T1
        test.Pins.Pin(1, externalOwner);

        // Advance several ticks so the epoch passes T1 and it enters the pinned queue.
        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.True(test.Pins.IsPinned(1));
        Assert.False(tick1Snapshot.IsUnreferenced);

        // Save pin cleared — external pin still holds.
        test.Save.CompletePendingSave();

        Assert.True(test.Pins.IsPinned(1));
        Assert.False(tick1Snapshot.IsUnreferenced);

        // External pin cleared — both pins gone.
        test.Pins.Unpin(1, externalOwner);

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        Assert.False(test.Pins.IsPinned(1));
        Assert.True(tick1Snapshot.IsUnreferenced);
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
        Assert.Equal(1, test.Shared.ConsumptionEpoch);
    }

    [Fact]
    public void UnpinnedSnapshot_IsReclaimedAfterConsumptionAdvancesPastIt()
    {
        var test = LoopHarness.Create();

        test.SimulationLoop.Bootstrap();

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // T1
        var tick1Snapshot = Assert.IsType<WorldSnapshot>(test.SimulationLoop.CurrentSnapshot);

        test.ConsumptionLoop.RunIteration(); // consume T1, epoch=1

        // Advance enough ticks that the epoch passes T1 and cleanup frees it.
        // With the one-tick-behind epoch model, an extra consumption iteration
        // is needed compared to the old model.
        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.True(tick1Snapshot.IsUnreferenced);
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
        Assert.Equal(1, test.Shared.ConsumptionEpoch);
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
        Assert.Equal(2, test.Shared.ConsumptionEpoch);
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

        var exception = Assert.Throws<InvalidOperationException>(
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

        var exception = Assert.Throws<InvalidOperationException>(
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
        test.SimulationLoop.RunIteration(); // T1
        var tick1Snapshot = Assert.IsType<WorldSnapshot>(test.SimulationLoop.CurrentSnapshot);

        test.ConsumptionLoop.RunIteration(); // consume T1, epoch=1, no save

        // Advance enough ticks that the epoch passes T1 and cleanup frees it.
        for (var i = 0; i < 3; i++)
        {
            test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
            test.SimulationLoop.RunIteration();
            test.ConsumptionLoop.RunIteration();
        }

        Assert.True(tick1Snapshot.IsUnreferenced);
        Assert.Equal(0, test.ConsumptionLoop.PendingSaveCount);
    }

    private sealed class LoopHarness
    {
        private readonly SimulationLoop<TestRecordingClock, TestNoOpWaiter>.TestAccessor _simulationAccessor;
        private readonly ConsumptionLoop<TestRecordingClock, TestNoOpWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestRecordingRenderer>.TestAccessor _consumptionAccessor;
        private long _simulationLastTime;
        private long _simulationAccumulator;

        private LoopHarness(
            MemorySystem memory,
            SharedState shared,
            TestClockState clockState,
            TestSaveRunnerState saveRunnerState,
            TestSaverState saverState,
            TestRendererState rendererState,
            SimulationLoop<TestRecordingClock, TestNoOpWaiter> simulationLoop,
            ConsumptionLoop<TestRecordingClock, TestNoOpWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestRecordingRenderer> consumptionLoop)
        {
            this.Memory = memory;
            this.Shared = shared;
            this.Clock = new ClockController(clockState);
            this.Pins = new PinController(memory.PinnedVersions);
            this.Save = new SaveController(saveRunnerState, saverState);
            this.ConsumptionLoop = new ConsumptionLoopController(consumptionLoop.GetTestAccessor(), saveRunnerState);
            this.SimulationLoop = new SimulationLoopController(this, simulationLoop.GetTestAccessor());
            this.Renderer = new RendererController(rendererState);

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

        public static LoopHarness Create(int poolSize = 8)
        {
            var memory = new MemorySystem(poolSize);
            var shared = new SharedState();
            var clockState = new TestClockState();
            var saveRunnerState = new TestSaveRunnerState();
            var saverState = new TestSaverState();
            var rendererState = new TestRendererState();
            var clock = new TestRecordingClock(clockState);
            var waiter = new TestNoOpWaiter();
            var saveRunner = new TestRecordingSaveRunner(saveRunnerState);
            var saver = new TestRecordingSaver(saverState);
            var renderer = new TestRecordingRenderer(rendererState);
            var simulationLoop = new SimulationLoop<TestRecordingClock, TestNoOpWaiter>(memory, shared, new SimulationCoordinator(1), clock, waiter);
            var consumptionLoop = new ConsumptionLoop<TestRecordingClock, TestNoOpWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestRecordingRenderer>(
                memory,
                shared,
                clock,
                waiter,
                saveRunner,
                saver,
                renderer,
                new RenderCoordinator(1));

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
            private readonly SimulationLoop<TestRecordingClock, TestNoOpWaiter>.TestAccessor _accessor;

            public SimulationLoopController(
                LoopHarness owner,
                SimulationLoop<TestRecordingClock, TestNoOpWaiter>.TestAccessor accessor)
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

            public void RunIteration() => _accessor.RunOneIteration(
                    CancellationToken.None,
                    ref _owner._simulationLastTime,
                    ref _owner._simulationAccumulator);

        }

        public sealed class ConsumptionLoopController
        {
            private readonly ConsumptionLoop<TestRecordingClock, TestNoOpWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestRecordingRenderer>.TestAccessor _accessor;
            private readonly TestSaveRunnerState _saveRunnerState;

            public ConsumptionLoopController(
                ConsumptionLoop<TestRecordingClock, TestNoOpWaiter, TestRecordingSaveRunner, TestRecordingSaver, TestRecordingRenderer>.TestAccessor accessor,
                TestSaveRunnerState saveRunnerState)
            {
                _accessor = accessor;
                _saveRunnerState = saveRunnerState;
            }

            public int PendingSaveCount => _saveRunnerState.PendingInvocations.Count;

            public void RunIteration() => _accessor.RunOneIteration(CancellationToken.None);
        }

        public sealed class ClockController
        {
            private readonly TestClockState _state;

            public ClockController(TestClockState state) => _state = state;

            public long NowNanoseconds => _state.NowNanoseconds;

            public void AdvanceBy(long nanoseconds) => _state.NowNanoseconds += nanoseconds;
        }

        public sealed class PinController
        {
            private readonly PinnedVersions _pins;

            public PinController(PinnedVersions pins) => _pins = pins;

            public void Pin(int tick, object owner) => _pins.Pin(tick, owner);

            public void Unpin(int tick, object owner) => _pins.Unpin(tick, owner);

            public bool IsPinned(int tick) => _pins.IsPinned(tick);
        }

        public sealed class SaveController
        {
            private readonly TestSaveRunnerState _state;
            private readonly TestSaverState _saverState;

            public SaveController(TestSaveRunnerState state, TestSaverState saverState)
            {
                _state = state;
                _saverState = saverState;
            }

            public int PendingSaveCount => _state.PendingInvocations.Count;

            public IReadOnlyList<int> SaveCalls => _saverState.SaveCalls;

            public void FailDispatchWith(Exception exception) => _state.ExceptionToThrow = exception;

            public void ClearDispatchFailure() => _state.ExceptionToThrow = null;

            public void CompletePendingSave()
            {
                var invocation = Assert.Single(_state.PendingInvocations);
                _state.PendingInvocations.Clear();
                _state.Complete(invocation);
            }
        }

        public sealed class RendererController
        {
            private readonly TestRendererState _state;

            public RendererController(TestRendererState state) => _state = state;

            public IReadOnlyList<int> RenderedTicks => _state.RenderedTicks;

            public void FailWith(Exception exception) => _state.ExceptionToThrow = exception;

            public void ClearFailure() => _state.ExceptionToThrow = null;
        }
    }

    private sealed class TestSaveRunnerState
    {
        public readonly List<TestSaveInvocation> DispatchHistory = [];
        public readonly List<TestSaveInvocation> PendingInvocations = [];

        public Exception? ExceptionToThrow { get; set; }

        public void Complete(TestSaveInvocation invocation) => invocation.SaveAction(invocation.Image, invocation.Tick);
    }

    private readonly struct TestRecordingSaveRunner : ISaveRunner
    {
        private readonly TestSaveRunnerState _state;

        public TestRecordingSaveRunner(TestSaveRunnerState state) => _state = state;

        public void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction)
        {
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;

            var invocation = new TestSaveInvocation(image, tick, saveAction);
            _state.DispatchHistory.Add(invocation);
            _state.PendingInvocations.Add(invocation);
        }
    }

    private sealed class TestSaverState
    {
        public readonly List<int> SaveCalls = [];
    }

    private readonly struct TestRecordingSaver : ISaver
    {
        private readonly TestSaverState _state;

        public TestRecordingSaver(TestSaverState state) => _state = state;

        public void Save(WorldImage image, int tick) => _state.SaveCalls.Add(tick);
    }

    private sealed class TestRendererState
    {
        public readonly List<int> RenderedTicks = [];

        public Exception? ExceptionToThrow { get; set; }
    }

    private readonly struct TestRecordingRenderer : IRenderer
    {
        private readonly TestRendererState _state;

        public TestRecordingRenderer(TestRendererState state) => _state = state;

        public void Render(in RenderFrame frame)
        {
            if (_state.ExceptionToThrow is Exception exception)
                throw exception;
            _state.RenderedTicks.Add(frame.Latest.TickNumber);
        }
    }
}
