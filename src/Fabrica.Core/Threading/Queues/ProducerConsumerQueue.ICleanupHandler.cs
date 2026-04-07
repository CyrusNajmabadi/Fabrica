namespace Fabrica.Core.Threading.Queues;

public sealed partial class ProducerConsumerQueue<T>
{
    /// <summary>
    /// Callback for <see cref="ProducerCleanup{THandler}"/>. Called once for each item being reclaimed. The handler should inspect
    /// the item and take appropriate action — typically checking whether it is pinned and either copying it to a side table for
    /// deferred processing, or releasing its resources.
    ///
    /// After the handler returns, the slab slot is cleared (<c>= default</c>) regardless of what the handler did. If the item must
    /// be preserved, the handler must copy it before returning.
    ///
    /// Constrained to struct for zero interface-dispatch overhead — the JIT specializes each call through the generic constraint.
    /// </summary>
    public interface ICleanupHandler
    {
        void HandleCleanup(long position, in T item);
    }
}
