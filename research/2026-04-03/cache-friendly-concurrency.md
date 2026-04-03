# Research: Cache-Friendly Concurrent Programming

*Conducted 2026-04-03. Focus on minimizing cache line bouncing and cross-core traffic.*

---

## Cache Coherence Fundamentals

### Protocols

- **MESI** (Modified/Exclusive/Shared/Invalid): A write invalidates all other copies. A read
  after a remote write forces a cache-to-cache transfer.
- **MOESI**: Adds Owned state — dirty shared copy reduces write-backs.
- **MESIF**: Adds Forward state — one Shared holder responds to reads, cuts redundant traffic.

### What Happens on Atomic Write + Remote Read

1. Writer needs line in Exclusive/Modified state → invalidates all Shared copies.
2. Reader sends read-for-share → coherence finds Modified copy → writer downgrades to Shared,
   supplies data. Transfer is cache-line granular (64B on x86, **128B on Apple Silicon**).

### Cost of Cache Line Transfer

No single universal number — depends on topology:
- **Same L2 / same CCX**: Low single-digit ns
- **Cross-CCX**: ~20-40ns
- **Cross-socket / NUMA**: ~60-100ns+
- **Contended atomic (many writers, one line)**: Effectively serialized regardless of core count

**Source**: Ramos & Hoefler, *Modeling Communication in Cache-Coherent SMP Systems*, HPDC 2013.
[PDF](https://htor.inf.ethz.ch/publications/img/hoefler-ramos-hpdc13-cc_modeling.pdf)

---

## The Cost of Atomics

### x86

- `LOCK ADD` on Skylake: ~18-20 cycles latency (uops.info)
- `LOCK CMPXCHG` on Alder Lake: ~14 cycles latency (uops.info)
- **Under contention**: Throughput collapses — one hot cache line serializes all cores.

### ARM (Apple Silicon)

- `LDXR/STXR` (exclusive load/store) pairs implement atomic RMW; failures require retry loops.
- LSE (Large System Extensions) atomics can be orders of magnitude faster than LL/SC loops.
- Apple Silicon cache line size: **128 bytes** (vs 64 bytes on x86).
- TSO emulation overhead on ARM: ~8.94% average (TOSTING study).

### Key Insight

The throughput limit of atomic operations on one cache line is:
`update_rate ≈ 1 / (average cycles per successful exclusive update)`
regardless of core count. This is why **sharding** (percpu_ref, biased RC, per-thread slots) is
the universal fix, not faster LOCK prefixes.

**Source**: Giesen (*ryg*), *Atomic operations and contention*, 2014.
(fgiesen.wordpress.com/2014/08/18/atomics-and-contention/)

---

## False Sharing

**Problem**: Independent writes to different variables in the same cache line force coherence
traffic as if the data were logically shared.

**Detection**: Linux `perf c2c`, Intel VTune Memory Access analysis.

**Real-world impact**: Fixing false sharing can yield 6-10x+ improvements. Jolt Physics PR #1136
reported 5-15% improvement from field reordering alone.

**Apple Silicon gotcha**: 128-byte cache lines mean false sharing radius is DOUBLE that of x86.
Padding must account for this.

**Source**: Sutter, *Eliminate False Sharing* (herbsutter.com/2009/05/15)
**Source**: Mario, *C2C - False Sharing Detection in Linux Perf* (joemario.github.io)

---

## Patterns That Minimize Cross-Core Traffic

### 1. Single-Writer Principle (LMAX Disruptor)

- Each sequence counter written by exactly one thread.
- Readers only observe — lines stay in Shared state across all reading cores.
- Disruptor throughput: ~26M ops/s vs ~5.3M ops/s for ArrayBlockingQueue.
- Latency: ~52ns mean vs ~32,757ns for ArrayBlockingQueue.

**Source**: LMAX Disruptor wiki (github.com/LMAX-Exchange/disruptor/wiki/Performance-Results)

### 2. Per-Thread Data Structures

- Linux per-CPU variables: `this_cpu_ops` (kernel.org)
- .NET: `[ThreadStatic]` (fast, static per-thread), `ThreadLocal<T>` (with factory/lifetime)
- Pattern: Update private counters; rare slow-path sums/publishes global snapshot.

### 3. Batching to Amortize Synchronization

- DPDK: 32/64-packet burst RX/TX processing.
- PostgreSQL: Group commit for WAL flushes.
- GC write barriers: Per-thread buffers flushed to collector.
- **Rule of thumb**: Maximum batch that still meets latency SLO.

### 4. Message Passing / SPSC Channels

- Actors (Erlang): Isolation + message copying. No shared mutable memory.
- SPSC queues: No CAS on either side on x86 (release stores compile to plain MOV).
- Asymmetric sync: Hot path does minimal work; cold path does heavy lifting (RCU pattern).

### 5. Flat Combining

- Threads post operations to a combining structure; one combiner executes all.
- Reduces cache line thrash vs naive lock ping-pong.
- Microsoft's snmalloc uses this for allocator scalability.

**Source**: Hendler et al., *Flat Combining and the Synchronization-Parallelism Tradeoff*, SPAA 2010.
[PDF](https://people.csail.mit.edu/shanir/publications/Flat%20Combining%20SPAA%2010.pdf)

---

## Apple Silicon Specifics

- Cache line size: **128 bytes** (query via `sysctl hw.cachelinesize`)
- Memory model: ARM weak ordering (weaker than x86 TSO)
- Atomic costs: Uncontended acquire/release reportedly competitive with x86 on M-series
- P-cores vs E-cores: Not NUMA, but heterogeneous scheduling matters for latency-sensitive work

**Source**: Apple Developer, *Addressing architectural differences in your macOS code*
**Source**: Go issue #53075 (github.com/golang/go/issues/53075)
