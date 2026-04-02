using Fabrica.Core.Threading;
using Xunit;

namespace Fabrica.Core.Tests.Threading;

public sealed class ThreadWaiterTests
{
    [Fact]
    public void Wait_WithZeroDuration_ReturnsImmediately()
    {
        var waiter = new ThreadWaiter();
        waiter.Wait(TimeSpan.Zero, CancellationToken.None);
    }

    [Fact]
    public void Wait_WithNegativeDuration_ReturnsImmediately()
    {
        var waiter = new ThreadWaiter();
        waiter.Wait(TimeSpan.FromTicks(-1), CancellationToken.None);
    }
}
