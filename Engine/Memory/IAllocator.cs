namespace Engine.Memory;

/// <summary>
/// Manages the lifecycle of pooled objects: allocation and pre-return cleanup.
/// Constrained to struct for zero interface-dispatch overhead via generic
/// specialisation — the JIT erases the indirection entirely.
/// </summary>
internal interface IAllocator<T> where T : class
{
    /// <summary>Creates a fresh instance for the pool.</summary>
    T Allocate();

    /// <summary>
    /// Resets an instance before it is returned to the pool.  Called by
    /// <see cref="ObjectPool{T, TAllocator}.Return"/> so domain-specific
    /// cleanup is guaranteed regardless of which code path returns the object.
    /// Implementations may be a no-op when no cleanup is needed.
    /// </summary>
    void Reset(T item);
}
