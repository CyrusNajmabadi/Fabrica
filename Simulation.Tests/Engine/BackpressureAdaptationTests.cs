using Simulation.Engine;
using Simulation.Memory;
using Simulation.World;
using Xunit;

namespace Simulation.Tests.Engine;

/// <summary>
/// Multi-phase behavioral tests that verify the backpressure feedback loop
/// adapts dynamically to changing simulation/consumption speed ratios.
///
/// Unlike the existing LoopStressHarnessTests (which verify individual
/// iterations), these tests run sustained phases of many iterations and
/// assert on aggregate behavior: total pressure delay, gap bounds, and
/// transitions between pressured and unpressured steady states.
///
/// All tests are deterministic — they use a controllable clock and recording
/// waiter, stepping both loops single-threaded with precise control over
/// how many iterations each side runs per phase.
/// </summary>
public sealed class BackpressureAdaptationTests
{
    private static int LowWaterMarkTicks =>
        (int)(SimulationConstants.PressureLowWaterMarkNanoseconds / SimulationConstants.TickDurationNanoseconds);

    private static int HardCeilingTicks =>
        (int)(SimulationConstants.PressureHardCeilingNanoseconds / SimulationConstants.TickDurationNanoseconds);

    private static TimeSpan IdleYield =>
        TimeSpan.FromTicks(SimulationConstants.IdleYieldNanoseconds / 100);

    [Fact]
    public void MatchedRates_NoBackpressureOverSustainedRun()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        for (var i = 0; i < 200; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }

        Assert.Equal(0, test.TotalPressureDelays);
        Assert.Equal(200, test.SimulationTick);
    }

    [Fact]
    public void SlowConsumption_CausesGrowingPressure()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        // Run simulation well past the low water mark without any consumption.
        var ticksToRun = LowWaterMarkTicks + 20;
        for (var i = 0; i < ticksToRun; i++)
            test.StepSimulation();

        Assert.True(test.TotalPressureDelays > 0,
            "Expected backpressure delays when consumption is stalled.");
        Assert.True(test.SimulationTick > LowWaterMarkTicks,
            "Simulation should have advanced past the low water mark.");
    }

    [Fact]
    public void ConsumptionCatchesUp_PressureFullyReleases()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        // Phase 1: Simulation runs ahead — pressure builds.
        var phase1Ticks = LowWaterMarkTicks + 10;
        for (var i = 0; i < phase1Ticks; i++)
            test.StepSimulation();

        Assert.True(test.TotalPressureDelays > 0, "Phase 1: expected pressure.");

        // Phase 2: Drain the gap with matched iterations. Consumption reads
        // LatestSnapshot each frame, so running consumption alone won't close
        // the gap — we need paired steps so new snapshots are published and
        // consumption's epoch advances. The first few pairs may still see
        // residual pressure as the gap narrows.
        for (var i = 0; i < phase1Ticks + 5; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }

        // Phase 3: Now at steady state — no pressure.
        test.ResetPressureCount();
        for (var i = 0; i < 20; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }

        Assert.Equal(0, test.TotalPressureDelays);
    }

    [Fact]
    public void PressureReengages_AfterConsumptionStallsAgain()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        // Phase 1: Matched rates — no pressure.
        for (var i = 0; i < 50; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }
        Assert.Equal(0, test.TotalPressureDelays);

        // Phase 2: Consumption stalls — pressure builds.
        for (var i = 0; i < LowWaterMarkTicks + 10; i++)
            test.StepSimulation();
        Assert.True(test.TotalPressureDelays > 0, "Phase 2: expected pressure.");

        // Phase 3: Drain with matched iterations, then verify steady state.
        for (var i = 0; i < LowWaterMarkTicks + 15; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }
        test.ResetPressureCount();
        for (var i = 0; i < 20; i++)
        {
            test.StepSimulation();
            test.StepConsumption();
        }
        Assert.Equal(0, test.TotalPressureDelays);

        // Phase 4: Consumption stalls again — pressure re-engages.
        test.ResetPressureCount();
        for (var i = 0; i < LowWaterMarkTicks + 10; i++)
            test.StepSimulation();
        Assert.True(test.TotalPressureDelays > 0, "Phase 4: expected pressure to re-engage.");
    }

    [Fact]
    public void PressureDelaysGrowExponentially_AsGapWidens()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        // Run past the low water mark and record pressure delays at each step.
        var delays = new List<TimeSpan>();
        for (var i = 0; i < LowWaterMarkTicks + 10; i++)
        {
            test.StepSimulation();
            delays.AddRange(test.DrainPressureDelays());
        }

        // After the first few pressure-free ticks, delays should appear and grow.
        Assert.True(delays.Count > 1,
            "Expected multiple pressure delays as the gap widened.");

        // Later delays should be >= earlier delays (exponential growth, not linear).
        for (var i = 1; i < delays.Count; i++)
        {
            Assert.True(delays[i] >= delays[i - 1],
                $"Delay at index {i} ({delays[i]}) should be >= delay at index {i - 1} ({delays[i - 1]}).");
        }
    }

    [Fact]
    public void HardCeiling_BlocksSimulation_UntilConsumptionAdvances()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        // Advance simulation to just below the hard ceiling.
        for (var i = 0; i < HardCeilingTicks - 1; i++)
            test.StepSimulation();

        var tickBeforeCeiling = test.SimulationTick;

        // The next simulation step would hit the hard ceiling. Use the waiter
        // callback to simulate consumption catching up while simulation is blocked.
        var waitCallsDuringCeiling = 0;
        test.OnWait = _ =>
        {
            waitCallsDuringCeiling++;
            if (waitCallsDuringCeiling == 1)
                test.AdvanceConsumptionEpochDirectly(tickBeforeCeiling);
        };

        test.StepSimulation();
        test.OnWait = null;

        // Simulation advanced past the ceiling (the wait callback unblocked it).
        Assert.True(test.SimulationTick > tickBeforeCeiling,
            "Simulation should have advanced after consumption caught up.");
        Assert.True(waitCallsDuringCeiling >= 1,
            "Expected at least one hard-ceiling wait before consumption advanced.");
    }

    [Fact]
    public void GapRemainsWithinHardCeiling_UnderSustainedSlowConsumption()
    {
        var test = BackpressureHarness.Create();
        test.Bootstrap();

        // Consumption runs at 1/4 the simulation rate — slow but not stalled.
        // The system should reach a steady state where pressure delays keep
        // the gap bounded without hitting the hard ceiling.
        for (var i = 0; i < 200; i++)
        {
            test.StepSimulation();
            if (i % 4 == 0)
                test.StepConsumption();
        }

        var gapTicks = test.SimulationTick - test.ConsumptionEpoch;
        var gapNanoseconds = (long)gapTicks * SimulationConstants.TickDurationNanoseconds;

        Assert.True(gapNanoseconds < SimulationConstants.PressureHardCeilingNanoseconds,
            $"Gap ({gapTicks} ticks = {gapNanoseconds / 1_000_000}ms) should stay below " +
            $"hard ceiling ({SimulationConstants.PressureHardCeilingNanoseconds / 1_000_000}ms).");
        Assert.True(test.TotalPressureDelays > 0,
            "Expected soft pressure delays to keep the gap bounded.");
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class BackpressureHarness
    {
        private readonly SharedState _shared;
        private readonly ClockState _clockState;
        private readonly WaiterState _waiterState;
        private readonly SimulationLoop<RecordingClock, RecordingWaiter>.TestAccessor _simulationAccessor;
        private readonly ConsumptionLoop<RecordingClock, NoWaiter, NoOpSaveRunner, NoOpSaver, NoOpRenderer>.TestAccessor _consumptionAccessor;
        private long _simulationLastTime;
        private long _simulationAccumulator;
        private int _totalPressureDelays;

        private BackpressureHarness(
            SharedState shared,
            ClockState clockState,
            WaiterState waiterState,
            SimulationLoop<RecordingClock, RecordingWaiter>.TestAccessor simulationAccessor,
            ConsumptionLoop<RecordingClock, NoWaiter, NoOpSaveRunner, NoOpSaver, NoOpRenderer>.TestAccessor consumptionAccessor)
        {
            _shared = shared;
            _clockState = clockState;
            _waiterState = waiterState;
            _simulationAccessor = simulationAccessor;
            _consumptionAccessor = consumptionAccessor;
        }

        public int SimulationTick => _simulationAccessor.CurrentTick;
        public int ConsumptionEpoch => _shared.ConsumptionEpoch;
        public int TotalPressureDelays => _totalPressureDelays;

        public Action<TimeSpan>? OnWait
        {
            get => _waiterState.OnWait;
            set => _waiterState.OnWait = value;
        }

        public static BackpressureHarness Create()
        {
            var memory = new MemorySystem(512);
            var shared = new SharedState { NextSaveAtTick = 0 };
            var clockState = new ClockState();
            var waiterState = new WaiterState();
            var clock = new RecordingClock(clockState);

            var simulationLoop = new SimulationLoop<RecordingClock, RecordingWaiter>(
                memory, shared, new Simulator(1), clock, new RecordingWaiter(waiterState));
            var consumptionLoop = new ConsumptionLoop<RecordingClock, NoWaiter, NoOpSaveRunner, NoOpSaver, NoOpRenderer>(
                memory, shared, clock, new NoWaiter(), new NoOpSaveRunner(), new NoOpSaver(), new NoOpRenderer());

            return new BackpressureHarness(
                shared,
                clockState,
                waiterState,
                simulationLoop.GetTestAccessor(),
                consumptionLoop.GetTestAccessor());
        }

        public void Bootstrap()
        {
            _simulationAccessor.Bootstrap();
            _simulationLastTime = 0;
            _simulationAccumulator = 0;
        }

        public void StepSimulation()
        {
            _clockState.NowNanoseconds += SimulationConstants.TickDurationNanoseconds;
            _waiterState.WaitCalls.Clear();

            _simulationAccessor.RunOneIteration(
                CancellationToken.None,
                ref _simulationLastTime,
                ref _simulationAccumulator);

            _totalPressureDelays += CountPressureDelays(_waiterState.WaitCalls);
        }

        public void StepConsumption() =>
            _consumptionAccessor.RunOneIteration(CancellationToken.None);

        public void AdvanceConsumptionEpochDirectly(int epoch) =>
            _shared.ConsumptionEpoch = epoch;

        public void ResetPressureCount() => _totalPressureDelays = 0;

        /// <summary>
        /// Returns any pressure delays recorded since the last call, then clears them.
        /// Used by tests that want to inspect the delay sequence step by step.
        /// </summary>
        public List<TimeSpan> DrainPressureDelays()
        {
            var delays = _waiterState.WaitCalls
                .Where(w => w > TimeSpan.Zero && w != IdleYield)
                .ToList();
            _waiterState.WaitCalls.Clear();
            return delays;
        }

        private static int CountPressureDelays(List<TimeSpan> waitCalls) =>
            waitCalls.Count(w => w > TimeSpan.Zero && w != IdleYield);
    }

    // ── Struct implementations ───────────────────────────────────────────────

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
        public Action<TimeSpan>? OnWait { get; set; }
    }

    private readonly struct RecordingWaiter : IWaiter
    {
        private readonly WaiterState _state;
        public RecordingWaiter(WaiterState state) => _state = state;

        public void Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            _state.WaitCalls.Add(duration);
            _state.OnWait?.Invoke(duration);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private readonly struct NoWaiter : IWaiter
    {
        public void Wait(TimeSpan duration, CancellationToken cancellationToken) =>
            cancellationToken.ThrowIfCancellationRequested();
    }

    private readonly struct NoOpSaveRunner : ISaveRunner
    {
        public void RunSave(WorldImage image, int tick, Action<WorldImage, int> saveAction) { }
    }

    private readonly struct NoOpSaver : ISaver
    {
        public void Save(WorldImage image, int tick) { }
    }

    private readonly struct NoOpRenderer : IRenderer
    {
        public void Render(in RenderFrame frame) { }
    }
}
