using System.Diagnostics;

namespace Fabrica.Core.Threading;

/// <summary>
/// Debug-only assertion that all mutating operations happen on a single thread. The first call to
/// <see cref="AssertOwnerThread"/> records the owner thread; subsequent calls assert the same thread.
///
/// In Release builds, calls to <see cref="AssertOwnerThread"/> are stripped entirely by the compiler
/// via <see cref="ConditionalAttribute"/>. The struct still occupies 4 bytes (the <c>int</c> field)
/// but no code executes.
///
/// Uses 0 as the "unset" sentinel since .NET managed thread IDs are always &gt;= 1.
/// </summary>
internal struct SingleThreadedOwner
{
    private int _ownerThreadId;

    [Conditional("DEBUG")]
    public void AssertOwnerThread()
    {
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0)
            _ownerThreadId = current;
        else
            Debug.Assert(
                _ownerThreadId == current,
                $"Single-threaded operation called from thread {current}, but owner is thread {_ownerThreadId}.");
    }
}
