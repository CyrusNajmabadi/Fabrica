# Fabrica

[![CI](https://github.com/CyrusNajmabadi/Fabrica/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/CyrusNajmabadi/Fabrica/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/CyrusNajmabadi/Fabrica/branch/master/graph/badge.svg)](https://codecov.io/gh/CyrusNajmabadi/Fabrica)

A simulation engine experiment, built for fun and learning. Inspired by factory automation games like Factorio.

## Goals

### Simulation independent of rendering

The simulation produces world state at a fixed tick rate (currently 40 ticks/sec). Rendering consumes the latest
available snapshot at its own pace (~60 fps). The two are temporally decoupled — the simulation is always running
ahead, and rendering always operates on already-computed, immutable data. Neither thread blocks the other's core work.

### Thread safety without locks on the hot path

The simulation and rendering hot paths are designed to be decoupled in how data flows between them, using lock-free
approaches in user-land to prevent one from stalling the other. No mutexes, no lock contention, no priority inversion —
though obviously not free from CPU cache coherence costs. Heavier concurrent machinery is reserved for infrequent
operations (like save coordination) that aren't on the per-tick or per-frame path.

### Scalable parallelism by design

The architecture is designed so that both simulation and rendering can be internally parallelized in the future without
changing the cross-thread contract:

- **Simulation workers** can read the current (immutable, published) world image and write into a fresh image that no
  other thread can see yet. Join before publish — no locks needed between workers.
- **Render workers** can read both the previous and current snapshots freely during a `Render` call, since both are
  guaranteed alive and immutable until the call returns.

### Graceful degradation under pressure

When the simulation outpaces consumption (e.g., rendering stalls), a back-pressure system kicks in:

- Pool usage is divided into buckets with exponentially increasing delays (1 ms → 2 ms → ... → 64 ms) that slow the
  simulation proportionally to pressure.
- Full pool exhaustion blocks the simulation until slots are freed, rather than allocating unboundedly.
- Stale epoch reads are always conservative — they retain memory slightly longer, never free prematurely.

### Object pooling and epoch-based reclamation

World snapshots and images are pooled and reused. The consumption thread advances an epoch; the simulation thread
reclaims anything the consumer has moved past. Snapshots pinned for async saves are spliced out of the reclamation chain
so they don't block cleanup of newer entries.

## Status

Early. The engine architecture (threading, memory management, save coordination, pressure) is in place and well-tested.
World state is still a placeholder — belts, machines, and actual game logic are next.

## Building

```
dotnet build
dotnet test
```

Targets .NET 10.
