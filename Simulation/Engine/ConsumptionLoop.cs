using System.Collections.Concurrent;
using Simulation.Memory;
using Simulation.World;

namespace Simulation.Engine;

/// <summary>
/// The consumption ("consumer") thread.  Processes the latest node at ≈60 fps
/// and coordinates periodic saves.
///
/// FRAME LOOP  (RunOneIteration)
///   1. DrainSaveEvents: process any completed save results from the threadpool.
///   2. Volatile-read LatestNode (acquire fence: all payload writes by the
///      production thread are now visible).
///   3. Rotate the node pair if a new node has arrived (see below).
///   4. MaybeStartSave: if a save is due and not in flight, pin the node
///      and dispatch to the save task.
///   5. Consume: the consumer processes the previous/latest pair for the
///      duration of the call only — it must not store them.
///   6. ConsumptionEpoch = _previous.SequenceNumber (or _latest's if no
///      previous exists yet).  This keeps both held nodes alive.
///   7. ThrottleToFrameRate: sleep for any remaining frame budget (≈16.67 ms).
///
/// PREVIOUS / LATEST MODEL
///   The loop holds two distinct node references: _previous and _latest.
///   When LatestNode changes (new ticks published), the pair rotates:
///   old latest becomes previous, new node becomes latest.  Between
///   rotations the pair is stable.
///
///   The full forward-linked chain from _previous to _latest is guaranteed
///   alive during the Consume call.  When production publishes multiple
///   nodes between frames, _previous and _latest may be several sequences
///   apart — the consumer can iterate every intermediate node via the
///   chain, or simply work with the two endpoints.
///
///   INVARIANT: when _previous is non-null, it is always a different object
///   reference from _latest.  The rotation guard guarantees this.
///
/// EPOCH ADVANCEMENT
///   The epoch is set to _previous.SequenceNumber when both nodes exist,
///   or _latest.SequenceNumber on the first frame (no previous yet).
///   Cleanup frees strictly below the epoch, so both _previous (sequence N,
///   not &lt; N) and _latest (sequence &gt; N) remain alive — along with the
///   entire chain between them.
///
/// SAVE PIN DISCIPLINE
///   The ordering within a save-triggering frame is load-bearing:
///
///     1. Pin(sequence)     ← epoch hasn't advanced; production holds node alive
///     2. RunSave(node)     ← dispatch to save task
///     3. Consume(…)        ← may throw; epoch still not advanced (safe)
///     4. ConsumptionEpoch  ← production may now look past older nodes
///
///   If RunSave throws (dispatch failure): pin is released, NextSaveAtTick is
///   restored to the current sequence, and the save will be retried next frame.
///   If Consume throws after a successful dispatch: the pin remains so the save
///   task can still safely read the node.  The save task's finally block always
///   clears the pin and reschedules NextSaveAtTick.
///
/// Generic constraints (all struct) eliminate interface dispatch on every call
/// in the hot frame loop.
/// </summary>
internal sealed class ConsumptionLoop<TNode, TConsumer, TSaveRunner, TSaver, TClock, TWaiter>
    where TNode : ChainNode<TNode>
    where TConsumer : struct, IConsumer<TNode>
    where TSaveRunner : struct, ISaveRunner<TNode>
    where TSaver : struct, ISaver<TNode>
    where TClock : struct, IClock
    where TWaiter : struct, IWaiter
{
    private readonly object _savePinOwner = new();
    private readonly ConcurrentQueue<SaveEvent> _saveEvents = new();

    private TNode? _previous;
    private TNode? _latest;

    private bool _saveInFlight;
    private SaveEvent? _lastSaveResult;

    private readonly PinnedVersions _pinnedVersions;
    private readonly SharedState<TNode> _shared;
    private TConsumer _consumer;
    private readonly TClock _clock;
    private readonly TWaiter _waiter;
    private TSaveRunner _saveRunner;
    private TSaver _saver;

    public ConsumptionLoop(
        PinnedVersions pinnedVersions,
        SharedState<TNode> shared,
        TConsumer consumer,
        TClock clock,
        TWaiter waiter,
        TSaveRunner saveRunner,
        TSaver saver)
    {
        _pinnedVersions = pinnedVersions;
        _shared = shared;
        _consumer = consumer;
        _clock = clock;
        _waiter = waiter;
        _saveRunner = saveRunner;
        _saver = saver;
    }

    public void Run(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (!cancellationToken.IsCancellationRequested)
            this.RunOneIteration(cancellationToken);
    }

    private void RunOneIteration(CancellationToken cancellationToken)
    {
        var frameStart = _clock.NowNanoseconds;

        this.DrainSaveEvents();

        var latestNode = _shared.LatestNode;
        if (latestNode is not null)
        {
            if (!ReferenceEquals(latestNode, _latest))
            {
                _previous = _latest;
                _latest = latestNode;
            }

            this.MaybeStartSave(_latest!);

            _consumer.Consume(
                _previous,
                _latest!,
                frameStart,
                new SaveStatus
                {
                    InFlight = _saveInFlight,
                    LastResult = _lastSaveResult,
                },
                cancellationToken);

            _shared.ConsumptionEpoch = _previous?.SequenceNumber
                                       ?? _latest!.SequenceNumber;
        }

        this.ThrottleToFrameRate(frameStart, cancellationToken);
    }

    private void DrainSaveEvents()
    {
        while (_saveEvents.TryDequeue(out var saveEvent))
        {
            _saveInFlight = false;
            _lastSaveResult = saveEvent;
        }
    }

    /// <summary>
    /// Starts a background save if the latest sequence has reached (or passed)
    /// the scheduled save tick and no save is currently in flight.
    ///
    /// Pin ordering: the node is pinned BEFORE RunSave is called.  At this
    /// point ConsumptionEpoch has not yet advanced past the sequence, so the
    /// production loop already holds the node alive via the epoch.  The pin
    /// extends that protection beyond the epoch advance that happens later in
    /// the same frame.  On dispatch failure the pin is immediately released
    /// and the save tick is restored so the next frame can retry.
    /// </summary>
    private void MaybeStartSave(TNode node)
    {
        var nextSaveAt = _shared.NextSaveAtTick;

        if (nextSaveAt == 0 || node.SequenceNumber < nextSaveAt)
            return;

        var seqToSave = node.SequenceNumber;

        _shared.NextSaveAtTick = 0;

        _pinnedVersions.Pin(seqToSave, _savePinOwner);

        try
        {
            _saveRunner.RunSave(node, seqToSave, this.RunSaveTask);
            _saveInFlight = true;
        }
        catch
        {
            _pinnedVersions.Unpin(seqToSave, _savePinOwner);
            _shared.NextSaveAtTick = seqToSave;
            throw;
        }
    }

    /// <summary>
    /// Executed on the threadpool (via ISaveRunner) to perform the actual save.
    ///
    /// Exceptions from the saver are caught and reported via the save event queue
    /// rather than propagating as unobserved task exceptions.  The consumption
    /// thread drains the queue each frame and surfaces the result through
    /// <see cref="SaveStatus"/> in the consumer frame.
    ///
    /// The finally block runs regardless of success or failure, guaranteeing:
    ///   - The pin is always released so production is never permanently stalled
    ///     waiting for a save that failed or was never completed.
    ///   - NextSaveAtTick is always rescheduled so saves resume after a failure.
    /// </summary>
    private void RunSaveTask(TNode node, int sequenceNumber)
    {
        var startTime = _clock.NowNanoseconds;
        try
        {
            _saver.Save(node, sequenceNumber);
            _saveEvents.Enqueue(new SaveEvent(sequenceNumber, DurationNanoseconds: _clock.NowNanoseconds - startTime, Error: null));
        }
        catch (Exception ex)
        {
            _saveEvents.Enqueue(new SaveEvent(sequenceNumber, DurationNanoseconds: _clock.NowNanoseconds - startTime, Error: ex));
        }
        finally
        {
            _pinnedVersions.Unpin(sequenceNumber, _savePinOwner);

            _shared.NextSaveAtTick = sequenceNumber + SimulationConstants.SaveIntervalTicks;
        }
    }

    private void ThrottleToFrameRate(long frameStart, CancellationToken cancellationToken)
    {
        var elapsed = Math.Max(0, _clock.NowNanoseconds - frameStart);
        var remaining = SimulationConstants.RenderIntervalNanoseconds - elapsed;
        if (remaining > 0)
            _waiter.Wait(new TimeSpan(remaining / 100), cancellationToken);
    }

    internal TestAccessor GetTestAccessor() => new(this);

    internal readonly struct TestAccessor
    {
        private readonly ConsumptionLoop<TNode, TConsumer, TSaveRunner, TSaver, TClock, TWaiter> _loop;

        public TestAccessor(ConsumptionLoop<TNode, TConsumer, TSaveRunner, TSaver, TClock, TWaiter> loop) => _loop = loop;

        public void RunOneIteration(CancellationToken cancellationToken) => _loop.RunOneIteration(cancellationToken);

        public void MaybeStartSave(TNode node) => _loop.MaybeStartSave(node);

        public void RunSaveTask(TNode node, int sequenceNumber) => _loop.RunSaveTask(node, sequenceNumber);

        public void ThrottleToFrameRate(long frameStart, CancellationToken cancellationToken) =>
            _loop.ThrottleToFrameRate(frameStart, cancellationToken);
    }
}
