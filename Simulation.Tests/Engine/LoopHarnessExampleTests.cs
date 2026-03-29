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

        test.SimulationLoop.Cleanup();

        Assert.True(tick1Snapshot.IsUnreferenced);
        Assert.Equal(0, tick1Image.TickNumber);
        Assert.Equal(0, test.SimulationLoop.PinnedQueueCount);
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
            RendererState rendererState,
            SimulationLoop<RecordingClock, NoWaiter> simulationLoop,
            ConsumptionLoop<RecordingClock, NoWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer> consumptionLoop)
        {
            Memory = memory;
            Shared = shared;
            Clock = new ClockController(clockState);
            Pins = new PinController(memory.PinnedVersions);
            Save = new SaveController(saveRunnerState);
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

        public static LoopHarness Create(int poolSize = 8)
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

            public void Cleanup() => _accessor.CleanupStaleSnapshots();
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

            public int PendingSaveCount => _saveRunnerState.RunCalls.Count;

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

            public bool IsPinned(int tick) => _pins.IsPinned(tick);
        }

        public sealed class SaveController
        {
            private readonly SaveRunnerState _state;

            public SaveController(SaveRunnerState state)
            {
                _state = state;
            }

            public void CompletePendingSave()
            {
                SaveInvocation invocation = Assert.Single(_state.RunCalls);
                _state.RunCalls.Clear();
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
        public readonly List<SaveInvocation> RunCalls = [];

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
            _state.RunCalls.Add(new SaveInvocation(image, tick, saveAction));
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
    }

    private readonly struct RecordingRenderer : IRenderer
    {
        private readonly RendererState _state;

        public RecordingRenderer(RendererState state)
        {
            _state = state;
        }

        public void Render(WorldSnapshot snapshot)
        {
            _state.RenderedTicks.Add(snapshot.Image.TickNumber);
        }
    }
}
