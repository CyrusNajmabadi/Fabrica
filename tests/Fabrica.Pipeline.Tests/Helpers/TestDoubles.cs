using Fabrica.Core.Memory;
using Fabrica.Core.Threading;
using Fabrica.Core.Threading.Queues;

namespace Fabrica.Pipeline.Tests.Helpers;

/// <summary>
/// Pipeline timing for tests — self-contained constants that mirror a typical simulation configuration without depending on
/// any engine-layer type.
/// </summary>
internal static class TestPipelineConfiguration
{
    public const long TickDurationNanoseconds = 25_000_000L;
    public const long IdleYieldNanoseconds = 1_000_000L;
    public const long PressureLowWaterMarkNanoseconds = 100_000_000L;
    public const long PressureHardCeilingNanoseconds = 2_000_000_000L;
    public const int PressureBucketCount = 8;
    public const long PressureBaseDelayNanoseconds = 1_000_000L;
    public const long PressureMaxDelayNanoseconds = 64_000_000L;
    public const long RenderIntervalNanoseconds = 16_666_666L;

    public static PipelineConfiguration Default => new()
    {
        TickDurationNanoseconds = TickDurationNanoseconds,
        IdleYieldNanoseconds = IdleYieldNanoseconds,
        PressureLowWaterMarkNanoseconds = PressureLowWaterMarkNanoseconds,
        PressureHardCeilingNanoseconds = PressureHardCeilingNanoseconds,
        PressureBucketCount = PressureBucketCount,
        PressureBaseDelayNanoseconds = PressureBaseDelayNanoseconds,
        PressureMaxDelayNanoseconds = PressureMaxDelayNanoseconds,
        RenderIntervalNanoseconds = RenderIntervalNanoseconds,
    };
}

internal sealed class TestClockState
{
    public long NowNanoseconds { get; set; }
}

internal readonly struct TestRecordingClock(TestClockState state) : IClock
{
    private readonly TestClockState _state = state;

    public long NowNanoseconds => _state.NowNanoseconds;
}

internal readonly struct TestFakeClock : IClock
{
    public long NowNanoseconds => 0;
}

internal sealed class TestWaiterState
{
    public readonly List<TimeSpan> WaitCalls = [];

    public Action? BeforeWait { get; set; }
    public Action<TimeSpan>? OnWait { get; set; }
}

internal readonly struct TestRecordingWaiter(TestWaiterState state) : IWaiter
{
    private readonly TestWaiterState _state = state;

    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        _state.WaitCalls.Add(duration);
        _state.BeforeWait?.Invoke();
        _state.OnWait?.Invoke(duration);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal readonly struct TestNoOpWaiter : IWaiter
{
    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
        => cancellationToken.ThrowIfCancellationRequested();
}

internal readonly struct TestNoOpConsumer : IConsumer<TestPayload>
{
    public void Consume(
        in ProducerConsumerQueue<PipelineEntry<TestPayload>>.Segment entries,
        long frameStartNanoseconds,
        CancellationToken cancellationToken)
    { }
}

// ── Worker-backed test doubles ──────────────────────────────────────────────

internal readonly struct EmptyWorkState;

/// <summary>
/// Trivial executor that performs a short spin-wait — enough non-zero work to exercise the full worker thread
/// signal/park/wake machinery without adding real computation.
/// </summary>
internal readonly struct SpinWorkExecutor : IThreadExecutor<EmptyWorkState>
{
    public void Prepare() { }

    public void Execute(in EmptyWorkState state, CancellationToken cancellationToken)
        => Thread.SpinWait(10);
}

/// <summary>
/// Test producer that mirrors what <c>SimulationProducer</c> does at the engine layer: rents a payload from an object pool,
/// dispatches parallel work through a <see cref="WorkerGroup{TState,TExecutor}"/>, and returns the payload. This ensures
/// pipeline-level stress tests exercise the full threading infrastructure.
/// </summary>
internal readonly struct TestWorkerProducer(ObjectPool<TestPayload, TestPayload.Allocator> payloadPool, int workerCount)
    : IProducer<TestPayload>
{
    private readonly ObjectPool<TestPayload, TestPayload.Allocator> _payloadPool = payloadPool;
    private readonly WorkerGroup<EmptyWorkState, SpinWorkExecutor> _workerGroup = new(
        workerCount, static _ => new SpinWorkExecutor(), "TestWorker");

    public TestPayload CreateInitialPayload(CancellationToken cancellationToken)
        => _payloadPool.Rent();

    public TestPayload Produce(TestPayload current, CancellationToken cancellationToken)
    {
        var payload = _payloadPool.Rent();
        _workerGroup.Dispatch(default, cancellationToken);
        return payload;
    }

    public void ReleaseResources(TestPayload payload)
        => _payloadPool.Return(payload);
}

internal readonly struct TestSimpleProducer : IProducer<TestPayload>
{
    public TestPayload CreateInitialPayload(CancellationToken cancellationToken)
        => default(TestPayload.Allocator).Allocate();

    public TestPayload Produce(TestPayload current, CancellationToken cancellationToken)
        => default(TestPayload.Allocator).Allocate();

    public void ReleaseResources(TestPayload payload) { }
}

/// <summary>
/// Test consumer that dispatches parallel work through a <see cref="WorkerGroup{TState,TExecutor}"/> on the consumption side,
/// exercising worker threads during frame processing.
/// </summary>
internal readonly struct TestWorkerConsumer(int workerCount) : IConsumer<TestPayload>
{
    private readonly WorkerGroup<EmptyWorkState, SpinWorkExecutor> _workerGroup = new(
        workerCount, static _ => new SpinWorkExecutor(), "TestConsumerWorker");

    public void Consume(
        in ProducerConsumerQueue<PipelineEntry<TestPayload>>.Segment entries,
        long frameStartNanoseconds,
        CancellationToken cancellationToken)
        => _workerGroup.Dispatch(default, cancellationToken);
}
