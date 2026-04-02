---
name: Generalize Memory and ChainNode
overview: Refactor ObjectPool to use a factory instead of `new()`, generalize MemorySystem on TPayload, constrain PinnedVersions owners to a marker interface, and nest ChainNode inside BaseProductionLoop for encapsulated mutation.
todos:
  - id: object-pool-factory
    content: Replace new() constraint on ObjectPool with Func<T> factory; update all call sites and tests
    status: completed
  - id: memory-system-generic
    content: Make MemorySystem<TPayload> generic, accept payload factory, wire into SimulationEngine.Create
    status: completed
  - id: pinned-versions-ipinowner
    content: Add IPinOwner marker interface, constrain PinnedVersions.Pin/Unpin, update ConsumptionLoop and tests
    status: completed
  - id: chainnode-nesting
    content: Nest ChainNode inside BaseProductionLoop<TPayload>, make mutation private, split ProductionLoop into base/derived, add TestAccessor
    status: pending
  - id: update-all-references
    content: Update all external ChainNode references (SharedState, ConsumptionLoop, IConsumer, RenderFrame, RenderConsumer, Host, all tests)
    status: pending
  - id: build-test-format
    content: Build, run all 117 tests, dotnet format --verify-no-changes, commit and push PR
    status: pending
isProject: false
---

# Generalize Memory Infrastructure and Encapsulate ChainNode Mutation

## 1. ObjectPool: Replace `new()` constraint with factory delegate

[Engine/Memory/ObjectPool.cs](Engine/Memory/ObjectPool.cs) currently constrains `T : class, new()` and calls `new T()` in both the constructor and `Rent()`. Replace with a `Func<T>` factory:

```csharp
internal sealed class ObjectPool<T> where T : class
{
    private readonly Stack<T> _items;
    private readonly Func<T> _factory;

    public ObjectPool(int initialCapacity, Func<T> factory)
    {
        _factory = factory;
        _items = new Stack<T>(initialCapacity);
        for (var i = 0; i < initialCapacity; i++)
            _items.Push(factory());
    }

    public T Rent()
    {
        // ...
        return _items.Count > 0 ? _items.Pop() : _factory();
    }
}
```

All call sites must pass a factory lambda:

- [Engine/Host.cs](Engine/Host.cs) `SimulationEngine.Create`: `new ObjectPool<ChainNode<WorldImage>>(..., () => new ChainNode<WorldImage>())`
- [Engine/Simulation/SimulationProducer.cs](Engine/Simulation/SimulationProducer.cs) (receives pool from factory, no change)
- Test files: `new ObjectPool<Dummy>(N, () => new Dummy())`, etc.

## 2. MemorySystem: Make generic on TPayload

[Engine/Memory/MemorySystem.cs](Engine/Memory/MemorySystem.cs) is currently hardcoded to `WorldImage`. It is not used by any production code path (`SimulationEngine.Create` builds pools directly), only by its own tests.

Two options:

- **Option A**: Make it `MemorySystem<TPayload>`, accept a `Func<TPayload>` payload factory, and wire it into `SimulationEngine.Create`.
- **Option B**: Delete it (it's unused in production) and keep pools created directly in the factory.

Recommendation: **Option A** -- generalize it and use it in `SimulationEngine.Create`, consolidating pool creation in one place.

```csharp
internal sealed class MemorySystem<TPayload> where TPayload : class
{
    private readonly ObjectPool<ChainNode<TPayload>> _nodePool;
    private readonly ObjectPool<TPayload> _payloadPool;

    public MemorySystem(int initialPoolSize, Func<TPayload> payloadFactory)
    {
        _nodePool = new ObjectPool<ChainNode<TPayload>>(initialPoolSize, () => new ChainNode<TPayload>());
        _payloadPool = new ObjectPool<TPayload>(initialPoolSize, payloadFactory);
    }

    public ChainNode<TPayload> RentNode() => _nodePool.Rent();
    public void ReturnNode(ChainNode<TPayload> node) => _nodePool.Return(node);

    public TPayload RentPayload() => _payloadPool.Rent();
    public void ReturnPayload(TPayload payload) => _payloadPool.Return(payload);
}
```

The `ReturnImage` method currently calls `image.ResetForPool()` before returning. With a generic payload, we need a way to reset arbitrary payloads. Options:

- Accept a `Action<TPayload>? resetAction` in the constructor or `ReturnPayload` method
- Have `IProducer.ReleaseResources` handle reset before returning (current pattern -- `SimulationProducer.ReleaseResources` already calls `payload.ResetForPool()` before `_imagePool.Return(payload)`)

Since the producer already handles reset, `MemorySystem.ReturnPayload` can stay simple (just pool return). The reset responsibility stays with the producer via `IProducer.ReleaseResources`.

## 3. PinnedVersions: Constrain owners to deferred consumers

Introduce a marker interface `IPinOwner` that `IDeferredConsumer<TPayload>` extends. [Engine/Memory/PinnedVersions.cs](Engine/Memory/PinnedVersions.cs) constrains `Pin`/`Unpin` to `IPinOwner` instead of `object`:

```csharp
// In Engine/Pipeline/IPinOwner.cs (or alongside IDeferredConsumer)
internal interface IPinOwner { }

// IDeferredConsumer extends it
internal interface IDeferredConsumer<in TPayload> : IPinOwner { ... }

// PinnedVersions
internal sealed class PinnedVersions
{
    public void Pin(int tick, IPinOwner owner) { ... }
    public void Unpin(int tick, IPinOwner owner) { ... }
    // IsPinned unchanged
}
```

In [Engine/Pipeline/ConsumptionLoop.cs](Engine/Pipeline/ConsumptionLoop.cs), remove the `_pinOwners` array and pass `_deferredConsumers[i]` directly as the pin owner (each `IDeferredConsumer<TPayload>` is its own identity via `IPinOwner`).

Tests in [Engine.Tests/Memory/PinnedVersionsTests.cs](Engine.Tests/Memory/PinnedVersionsTests.cs) would use a small test type implementing `IPinOwner` instead of `object`.

## 4. Nest ChainNode inside BaseProductionLoop for encapsulated mutation

This is the most significant change. The goal: **make all mutation on ChainNode private, accessible only to the production loop, while keeping the read-only surface public.**

### Mechanism

C# rule: an enclosing type can access the **private** members of its nested types. So if `ChainNode` is nested inside `BaseProductionLoop<TPayload>`, only `BaseProductionLoop` can call mutation methods. Code outside (ConsumptionLoop, IConsumer, RenderFrame, tests) sees only the public read-only surface.

### Structure

```
BaseProductionLoop<TPayload>           (abstract, owns chain management)
├── ChainNode                          (sealed, non-generic — gets TPayload from enclosing)
│   ├── public: SequenceNumber, PublishTimeNanoseconds, Payload (get-only)
│   ├── public: Chain(), ChainSegment, Enumerator (read-only iteration)
│   └── private: _next, _refCount, InitializeBase, SetNext, ClearNext, etc.
└── protected: AllocateNode, LinkAndPublish, FreeNode, CleanupStaleNodes, etc.

ProductionLoop<TPayload, TProducer, TClock, TWaiter> : BaseProductionLoop<TPayload>
└── Tick loop, accumulator, backpressure (calls protected base methods)
```

### Public surface of ChainNode (what consumers see)

- `SequenceNumber` (get)
- `PublishTimeNanoseconds` (get)
- `Payload` (get)
- `static Chain(start, end)` returning `ChainSegment` (zero-alloc iterator)

### Private surface (only BaseProductionLoop can access)

- `_next`, `_refCount`
- `InitializeBase(int)`, `MarkPublished(long)`, `SetNext(ChainNode)`, `ClearNext()`, `ClearPayload()`, `AddRef()`, `Release()`, `IsUnreferenced`
- `NextInChain` (for cleanup traversal)

### External type references

All types outside the production loop reference `BaseProductionLoop<TPayload>.ChainNode`:

- `SharedState<TPayload>` fields
- `ConsumptionLoop` fields and parameters
- `IConsumer<TPayload>.Consume` parameters
- `IDeferredConsumer<TPayload>.ConsumeAsync` (receives `TPayload` directly, unaffected)
- `RenderFrame` properties (concrete: `BaseProductionLoop<WorldImage>.ChainNode`)

This is verbose but self-documenting. Files in the pipeline namespace can use a file-scoped `using` alias for brevity in the concrete `WorldImage` case:

```csharp
using WorldChainNode = Engine.Pipeline.BaseProductionLoop<Engine.World.WorldImage>.ChainNode;
```

### Testing concern

Currently [Engine.Tests/World/ChainNodeTests.cs](Engine.Tests/World/ChainNodeTests.cs) directly constructs `ChainNode<WorldImage>` and calls `InitializeBase`, `SetNext`, etc. With private mutation, these tests would need to go through `BaseProductionLoop`. Two approaches:

- **A)** `BaseProductionLoop` exposes a `TestAccessor` that wraps node creation/mutation for tests (consistent with existing `ProductionLoop.TestAccessor` pattern).
- **B)** ChainNode tests are folded into ProductionLoop tests, since the node lifecycle is fundamentally a production loop concern.

Recommendation: **A** -- keep a focused `TestAccessor` on `BaseProductionLoop` for node-level unit testing.

### Key implementation note

Derived classes of the enclosing type (`ProductionLoop`) do **not** get access to `ChainNode`'s private members in C#. Only the directly declaring type (`BaseProductionLoop`) can. Therefore, all chain management logic (node init, linking, cleanup, freeing) must live as concrete methods in `BaseProductionLoop`, with `ProductionLoop` calling them.

## Files changed (estimated)

**Production code:**

- `Engine/Memory/ObjectPool.cs` -- remove `new()`, add factory
- `Engine/Memory/MemorySystem.cs` -- make generic
- `Engine/Memory/PinnedVersions.cs` -- `IPinOwner` constraint
- `Engine/Pipeline/ChainNode.cs` -- becomes nested in new base class file
- `Engine/Pipeline/BaseProductionLoop.cs` -- new file, or rename ChainNode.cs
- `Engine/Pipeline/ProductionLoop.cs` -- inherit from base, delegate chain ops
- `Engine/Pipeline/ConsumptionLoop.cs` -- update ChainNode references, remove `_pinOwners`
- `Engine/Pipeline/SharedState.cs` -- update ChainNode references
- `Engine/Pipeline/IConsumer.cs` -- update ChainNode references
- `Engine/Pipeline/IDeferredConsumer.cs` -- extend `IPinOwner`
- `Engine/Pipeline/IProducer.cs` -- no change (works with TPayload, not ChainNode)
- `Engine/Rendering/RenderFrame.cs` -- update ChainNode references
- `Engine/Rendering/RenderConsumer.cs` -- update ChainNode references
- `Engine/Host.cs` -- update types, pool factory lambdas
- `Engine/World/WorldImage.cs` -- update doc comment

**Test code (all 15 files):**

- Update `ChainNode<WorldImage>` references to `BaseProductionLoop<WorldImage>.ChainNode`
- Update `ObjectPool` construction to pass factories
- Update `PinnedVersions` tests to use `IPinOwner`
- Update `MemorySystem` tests for generic API
- Update `ChainNodeTests` to use TestAccessor

