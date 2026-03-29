using Simulation.Engine;
using Simulation.Memory;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

public sealed class LoopStressHarnessTests
{
    [Fact]
    public void SimulationIteration_RecoversFromSnapshotStarvation_AfterConsumptionAdvancesEpochDuringWait()
    {
        var test = LoopStressHarness.Create();

        test.SimulationLoop.Bootstrap();
        test.Shared.NextSaveAtTick = 0;

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 1
        test.ConsumptionLoop.RunIteration(); // epoch 1

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration(); // tick 2

        test.Memory.DrainSnapshots();
        Assert.Equal(0, test.Memory.AvailableSnapshots);
        test.Waiter.ClearCalls();

        int waitCount = 0;
        test.Waiter.OnWait = duration =>
        {
            waitCount++;

            if (waitCount == 1)
                test.ConsumptionLoop.RunIteration(); // epoch 2 while simulation is under pressure
        };

        test.Clock.AdvanceBy(SimulationConstants.TickDurationNanoseconds);
        test.SimulationLoop.RunIteration();

        Assert.Equal(3, test.SimulationLoop.CurrentTick);
        Assert.Equal(3, Assert.IsType<WorldSnapshot>(test.SimulationLoop.CurrentSnapshot).Image.TickNumber);
        Assert.Equal(2, test.Shared.ConsumptionEpoch);
        Assert.Equal(3, test.Waiter.WaitCalls.Count);
        Assert.Equal(GetExpectedPressureDelay(availableSnapshots: 0, capacity: test.Memory.SnapshotPoolCapacity), test.Waiter.WaitCalls[0]);
        Assert.Equal(GetPoolRetryWait(), test.Waiter.WaitCalls[1]);
        Assert.Equal(GetPoolRetryWait(), test.Waiter.WaitCalls[2]);
    }

    private static TimeSpan GetPoolRetryWait() =>
        TimeSpan.FromTicks(SimulationConstants.PoolEmptyRetryNanoseconds / 100);

    private static TimeSpan GetExpectedPressureDelay(int availableSnapshots, int capacity) =>
        TimeSpan.FromTicks(
            SimulationPressure.ComputeDelay(
                available: availableSnapshots,
                capacity: capacity,
                baseNanoseconds: SimulationConstants.PressureBaseDelayNanoseconds,
                maxNanoseconds: SimulationConstants.PressureMaxDelayNanoseconds) / 100);

    private sealed class LoopStressHarness
    {
        private long _simulationLastTime;
        private long _simulationAccumulator;

        private LoopStressHarness(
            MemorySystem memory,
            SharedState shared,
            ClockState clockState,
            WaiterState waiterState,
            SimulationLoop<RecordingClock, RecordingWaiter> simulationLoop,
            ConsumptionLoop<RecordingClock, NoWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer> consumptionLoop)
        {
            Shared = shared;
            Clock = new ClockController(clockState);
            Waiter = new WaiterController(waiterState);
            Memory = new MemoryController(memory);
            SimulationLoop = new SimulationLoopController(this, simulationLoop.GetTestAccessor());
            ConsumptionLoop = new ConsumptionLoopController(consumptionLoop.GetTestAccessor());
        }

        public SharedState Shared { get; }

        public ClockController Clock { get; }

        public WaiterController Waiter { get; }

        public MemoryController Memory { get; }

        public SimulationLoopController SimulationLoop { get; }

        public ConsumptionLoopController ConsumptionLoop { get; }

        public static LoopStressHarness Create(int poolSize = SimulationConstants.PressureBucketCount)
        {
            var memory = new MemorySystem(poolSize);
            var shared = new SharedState();
            var clockState = new ClockState();
            var waiterState = new WaiterState();
            var clock = new RecordingClock(clockState);
            var simulationLoop = new SimulationLoop<RecordingClock, RecordingWaiter>(
                memory,
                shared,
                clock,
                new RecordingWaiter(waiterState));
            var consumptionLoop = new ConsumptionLoop<RecordingClock, NoWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer>(
                memory,
                shared,
                clock,
                new NoWaiter(),
                new RecordingSaveRunner(),
                new RecordingSaver(),
                new RecordingRenderer());

            return new LoopStressHarness(
                memory,
                shared,
                clockState,
                waiterState,
                simulationLoop,
                consumptionLoop);
        }

        public sealed class ClockController
        {
            private readonly ClockState _state;

            public ClockController(ClockState state)
            {
                _state = state;
            }

            public void AdvanceBy(long nanoseconds) => _state.NowNanoseconds += nanoseconds;
        }

        public sealed class WaiterController
        {
            private readonly WaiterState _state;

            public WaiterController(WaiterState state)
            {
                _state = state;
            }

            public IReadOnlyList<TimeSpan> WaitCalls => _state.WaitCalls;

            public Action<TimeSpan>? OnWait
            {
                get => _state.OnWait;
                set => _state.OnWait = value;
            }

            public void ClearCalls() => _state.WaitCalls.Clear();
        }

        public sealed class MemoryController
        {
            private readonly MemorySystem _memory;

            public MemoryController(MemorySystem memory)
            {
                _memory = memory;
            }

            public int SnapshotPoolCapacity => _memory.SnapshotPoolCapacity;

            public int AvailableSnapshots
            {
                get
                {
                    var drained = new List<WorldSnapshot>();
                    while (_memory.RentSnapshot() is WorldSnapshot snapshot)
                        drained.Add(snapshot);

                    foreach (WorldSnapshot snapshot in drained)
                        _memory.ReturnSnapshot(snapshot);

                    return drained.Count;
                }
            }

            public List<WorldSnapshot> DrainSnapshots()
            {
                var drained = new List<WorldSnapshot>();

                while (_memory.RentSnapshot() is WorldSnapshot snapshot)
                    drained.Add(snapshot);

                return drained;
            }
        }

        public sealed class SimulationLoopController
        {
            private readonly LoopStressHarness _owner;
            private readonly SimulationLoop<RecordingClock, RecordingWaiter>.TestAccessor _accessor;

            public SimulationLoopController(
                LoopStressHarness owner,
                SimulationLoop<RecordingClock, RecordingWaiter>.TestAccessor accessor)
            {
                _owner = owner;
                _accessor = accessor;
            }

            public int CurrentTick => _accessor.CurrentTick;

            public WorldSnapshot? CurrentSnapshot => _accessor.CurrentSnapshot;

            public WorldSnapshot? OldestSnapshot => _accessor.OldestSnapshot;

            public void Bootstrap()
            {
                _accessor.Bootstrap();
                _owner._simulationLastTime = 0;
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

            public ConsumptionLoopController(
                ConsumptionLoop<RecordingClock, NoWaiter, RecordingSaveRunner, RecordingSaver, RecordingRenderer>.TestAccessor accessor)
            {
                _accessor = accessor;
            }

            public void RunIteration() => _accessor.RunOneIteration(CancellationToken.None);
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

    private sealed class WaiterState
    {
        public readonly List<TimeSpan> WaitCalls = [];

        public Action<TimeSpan>? OnWait { get; set; }
    }

    private readonly struct RecordingWaiter : IWaiter
    {
        private readonly WaiterState _state;

        public RecordingWaiter(WaiterState state)
        {
            _state = state;
        }

        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            _state.WaitCalls.Add(duration);
            _state.OnWait?.Invoke(duration);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private readonly struct NoWaiter : IWaiter
    {
        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private readonly struct RecordingSaveRunner : ISaveRunner
    {
        public void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction) =>
            throw new InvalidOperationException("Save dispatch is not part of this stress harness.");
    }

    private readonly struct RecordingSaver : ISaver
    {
        public void Save(WorldImage image, int tick) =>
            throw new InvalidOperationException("Save execution is not part of this stress harness.");
    }

    private readonly struct RecordingRenderer : IRenderer
    {
        public void Render(WorldSnapshot snapshot)
        {
        }
    }
}
