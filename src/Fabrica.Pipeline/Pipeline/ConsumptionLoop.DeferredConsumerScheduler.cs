using System.Diagnostics;

namespace Fabrica.Pipeline;

public sealed partial class ConsumptionLoop<TPayload, TConsumer, TClock, TWaiter>
{
    /// <summary>
    /// Encapsulates all state and logic for scheduling and dispatching deferred consumers (e.g. periodic saves). Keeps the main
    /// consumption loop lean.
    ///
    /// Uses a min-heap (<see cref="PriorityQueue{TElement,TPriority}"/>) keyed by the next-run wall-clock timestamp. Each frame
    /// the consumption loop calls <see cref="MaybeRunConsumers"/>; a single O(1) peek determines whether any consumer is due —
    /// no virtual calls or iteration when nothing is scheduled.
    ///
    /// Pins queue positions (not ChainNode sequence numbers) via <see cref="PinnedVersions"/>. The production thread's cleanup
    /// handler checks these pins to decide whether to release or stash each payload.
    /// </summary>
    private sealed class DeferredConsumerScheduler(
        PinnedVersions pinnedVersions,
        IDeferredConsumer<TPayload>[] consumers)
    {
        private readonly PinnedVersions _pinnedVersions = pinnedVersions;
        private readonly IDeferredConsumer<TPayload>[] _consumers = consumers;
        private readonly Task<long>?[] _inFlightTasks = new Task<long>?[consumers.Length];
        private readonly long[] _pinnedPositions = new long[consumers.Length];
        private readonly PriorityQueue<int, long> _schedule = new(consumers.Length);
        private bool _initialized;

        public void EnsureScheduleInitialized(long nowNanoseconds)
        {
            if (_initialized)
                return;
            _initialized = true;

            for (var i = 0; i < _consumers.Length; i++)
                _schedule.Enqueue(i, nowNanoseconds + _consumers[i].InitialDelayNanoseconds);
        }

        public void DrainCompletedTasks(long frameStartNanoseconds)
        {
            for (var i = 0; i < _inFlightTasks.Length; i++)
            {
                var task = _inFlightTasks[i];
                if (task is null || !task.IsCompleted)
                    continue;

                _pinnedVersions.Unpin(_pinnedPositions[i], _consumers[i]);
                _inFlightTasks[i] = null;

                if (task.IsFaulted)
                {
                    Debug.WriteLine(
                        $"Deferred consumer {i} ({_consumers[i].GetType().Name}) faulted: " +
                        $"{task.Exception?.InnerException?.Message ?? task.Exception?.Message}");
                }

                var nextRunTime = task.IsCompletedSuccessfully
                    ? task.Result
                    : frameStartNanoseconds + _consumers[i].ErrorRetryDelayNanoseconds;
                _schedule.Enqueue(i, nextRunTime);
            }
        }

        public void MaybeRunConsumers(
            in PipelineEntry<TPayload> latestEntry,
            long latestPosition,
            long frameStartNanoseconds,
            CancellationToken cancellationToken)
        {
            while (_schedule.TryPeek(out var consumerIndex, out var nextRun))
            {
                if (nextRun > frameStartNanoseconds)
                    break;

                _schedule.Dequeue();

                Debug.Assert(
                    _inFlightTasks[consumerIndex] is null,
                    $"Deferred consumer {consumerIndex} is scheduled to run but already has an in-flight task.");

                _pinnedVersions.Pin(latestPosition, _consumers[consumerIndex]);
                _pinnedPositions[consumerIndex] = latestPosition;

                try
                {
                    _inFlightTasks[consumerIndex] = _consumers[consumerIndex]
                        .ConsumeAsync(latestEntry.Payload, cancellationToken);
                }
                catch
                {
                    _pinnedVersions.Unpin(latestPosition, _consumers[consumerIndex]);
                    _schedule.Enqueue(consumerIndex, frameStartNanoseconds + _consumers[consumerIndex].ErrorRetryDelayNanoseconds);
                    throw;
                }
            }
        }
    }
}
