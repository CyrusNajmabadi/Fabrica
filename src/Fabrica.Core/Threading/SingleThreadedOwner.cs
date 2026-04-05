using System.Diagnostics;

namespace Fabrica.Core.Threading;

/// <summary>
/// Debug-only assertion that all mutating operations happen on a single thread. The first call to
/// <see cref="AssertOwnerThread"/> records the owner thread; subsequent calls assert the same thread.
///
/// In Release builds, the struct has no fields (zero storage cost) and all method calls are
/// stripped by <see cref="ConditionalAttribute"/>. Types that embed this struct should wrap
/// the field in <c>#if DEBUG</c> so it doesn't occupy space in Release.
///
/// Uses 0 as the "unset" sentinel since .NET managed thread IDs are always &gt;= 1.
/// </summary>
internal struct SingleThreadedOwner
{
#if DEBUG
    private int _ownerThreadId;
#endif

    [Conditional("DEBUG")]
    public void AssertOwnerThread()
    {
#if DEBUG
        var current = Environment.CurrentManagedThreadId;
        if (_ownerThreadId == 0)
            _ownerThreadId = current;
        else
            Debug.Assert(
                _ownerThreadId == current,
                $"Single-threaded operation called from thread {current}, but owner is thread {_ownerThreadId}.");
#endif
    }
}
