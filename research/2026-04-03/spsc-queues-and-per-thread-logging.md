# Research: SPSC Queues & Per-Thread Logging Patterns

*Conducted 2026-04-03. Focus on per-worker logging buffers for deferred operations.*

---

## Core Pattern

Each worker thread has a dedicated SPSC ring buffer. It logs operations (e.g., "release object X")
locally. A single collector thread drains all buffers and processes the logged operations. This
avoids ALL atomic RMW operations on the producer (worker) side.

---

## SPSC Queue Mechanics

### Lamport's SPSC Ring Buffer

- Circular array with two indices: **head** (consumer) and **tail** (producer).
- Producer writes element, then **release-store** tail. Consumer reads element, then
  **release-store** head.
- Each index is written by exactly one side → **no CAS needed**.

### Memory Ordering

- **x86 (TSO)**: Release stores and acquire loads map to **plain MOV** instructions. No `LOCK`
  prefix. The hardware memory model provides sufficient ordering.
- **ARM**: Compilers emit `STLR` (store-release) and `LDAR` (load-acquire). More expensive than
  plain stores but still much cheaper than CAS.
- **Portable C#**: Use `Volatile.Write` for tail, `Volatile.Read` for head.

**Source**: Cambridge C++11-to-hardware mappings (cl.cam.ac.uk/~pes20/cpp/cpp0xmappings.html)
**Source**: Rigtorp, *Optimizing a Ring Buffer for Throughput* (rigtorp.se/ringbuffer)

### Performance

- Rigtorp reports ~5.5M→112M items/s improvement from cache-local + cached index design (Ryzen
  3900X, int-sized elements).
- Alec Fessler's `libspscq`: Hundreds of millions of ops/s on Zen 4.

---

## Batched SPSC Writes

Instead of one volatile tail update per item, fill N slots then publish once:

1. Write items `buffer[tail..tail+N]` with plain stores.
2. Single `Volatile.Write(ref _tail, tail + N)`.

**Tradeoffs**:
- **Pro**: Fewer cache line bounces on the tail index. Higher throughput.
- **Con**: Increased latency (consumer sees batch only at end). Coarser back-pressure.

**Optimal for Fabrica**: Workers accumulate refcount-release events during job execution, then flush
the batch at job completion. One volatile write per job, not per released object.

---

## Real-World Per-Thread Logging

### Go GC: Per-P Write Barrier Buffers

- Each P (processor) has a `wbBuf` for write barrier fast path.
- Overflow flushes to GC structures.
- 4x faster barriers vs global approach.

**Source**: Go `runtime/mwbbuf.go`, issue #22460

### OpenJDK G1: Per-Thread SATB Queues

- Mutators enqueue pre-update references into `PtrQueue` / `ObjPtrQueue`.
- Buffers flush into global set for GC processing.
- Classic "buffer locally, drain centrally" pattern.

**Source**: OpenJDK `ptrQueue.hpp` (jdk8 tree)

### ZGC: Colored Pointers + Load Barrier

- Different tradeoff: barrier on reference use, not store.
- Not a "log every store" design.

**Source**: JEP 333 (openjdk.org/jeps/333)

### Linux Kernel: Lockless Ring Buffer (ftrace)

- Per-CPU circular buffers. Sequential writers per CPU.
- Overwrite vs drop modes for buffer-full.
- Background reader drains asynchronously.

**Source**: Kernel doc: *Lockless Ring Buffer Design* (kernel.org)

### Java Flight Recorder

- Thread-local buffers + global buffer system.
- Hot path: append-only to thread-local storage. Asynchronous drain.

**Source**: JEP 328 (openjdk.org/jeps/328)

---

## Asymmetric Synchronization

The unifying concept: **hot path** (worker) does minimal synchronization; **cold path** (collector)
does more work. Examples:

- **RCU**: Readers take no locks. Updaters pay for grace periods.
- **SPSC logging**: Producer does one release store. Consumer processes N items.
- **Disruptor**: Producers/consumers asymmetric in wait strategy cost.
- **Flat combining**: Threads post operations; one combiner executes all.

---

## .NET Specifics

### System.Threading.Channels

- `ChannelOptions.SingleReader` / `SingleWriter` hints exist.
- `SingleReader=true` selects `SingleConsumerUnboundedChannel` (specialized).
- General-purpose async pipeline — not bare-metal SPSC ring.
- Overhead from completion/cancellation coordination.

### For Fabrica

A custom SPSC ring buffer (similar to `ProducerConsumerQueue` already in codebase) with batched
push is likely better than `Channel<T>` for this use case. Key differences from current PCQ:

- **Batched push**: Write N items, one volatile tail update.
- **No consumer wake**: Consumer (cleanup job) runs at known phase boundaries, not on-demand.
- **Fixed-size ring**: Bounded, no growth needed if sized to max-items-per-tick.

---

## Sources

- Lamport, *On Interprocess Communication*, 1986
  [PDF](https://lamport.azurewebsites.net/pubs/interprocess.pdf)
- Rigtorp, SPSC ring buffer (rigtorp.se/ringbuffer)
- LMAX Disruptor wiki (github.com/LMAX-Exchange/disruptor/wiki)
- DPDK Ring Programmer's Guide (doc.dpdk.org)
- Hendler et al., *Flat Combining*, SPAA 2010
  [PDF](https://people.csail.mit.edu/shanir/publications/Flat%20Combining%20SPAA%2010.pdf)
