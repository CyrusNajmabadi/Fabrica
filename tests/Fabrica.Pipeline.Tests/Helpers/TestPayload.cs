using Fabrica.Core.Memory;

namespace Fabrica.Pipeline.Tests.Helpers;

/// <summary>
/// Minimal payload type for pipeline tests. Proves the pipeline layer is truly independent of any domain-specific payload
/// such as WorldImage.
/// </summary>
internal sealed class TestPayload
{
    public readonly struct Allocator : IAllocator<TestPayload>
    {
        public readonly TestPayload Allocate() => new();

        public readonly void Reset(TestPayload item) { }
    }
}
