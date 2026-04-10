---
name: Bisect race condition
overview: Two-phase bisection to find the PR that introduced the ~4% hang rate in Fabrica.Core.Tests. Phase 1 uses git bisect with each commit's own tests. Phase 2 backports the specific failing test to find the true origin.
todos:
  - id: phase1-setup
    content: "Phase 1 setup: write bisect script, establish good/bad bounds, start git bisect"
    status: completed
  - id: phase1-run
    content: "Phase 1 run: git bisect through history using each commit's own tests (100x, abort on first hang)"
    status: completed
  - id: phase1-result
    content: "Phase 1 result: identify which commit first shows the hang, and which specific test is hanging"
    status: completed
  - id: phase2-backport
    content: "Phase 2: take the specific hanging test backwards through earlier commits to find the true bug origin"
    status: completed
  - id: confirm
    content: "Confirm: verify the commit before the bug is truly clean (3x100 runs)"
    status: completed
  - id: analyze
    content: "Analyze: diff the culprit PR to identify the exact race condition"
    status: completed
isProject: false
---

# Bisect the WorkerPool Race Condition

## Background

Running `Fabrica.Core.Tests` 100x shows ~4% hang rate. The hang is always at 1264/1276 tests, with 3 test collections stuck (burning 100% CPU). It reproduces with and without `UNSAFE_OPT`, on macOS ARM64. The missing tests are always from `JobPoolTests` (which never get to run because parallel collections are stuck spinning).

## Key Observations

- The `BoundedLocalQueue` was introduced in PR #169 and went through major iterations (#169-#179)
- The original 3 scheduler stress tests have existed since PR #122
- 5 newer stress tests were added in PRs #194/#195
- Before PR #175, `WorkerPool` used a different `WorkStealingDeque`

## Two-Phase Strategy

### Phase 1: Find the first commit that hangs (using each commit's OWN tests)

No test backporting needed. Pure `git bisect`.

- **Bad (upper bound)**: `5cf9915` (current master, PR #197) -- confirmed HANGS
- **Good (lower bound)**: needs verification, likely PR #168 or earlier (before BLQ)
- ~30 PRs in range = ~5 binary search steps

**Bisect script** (run at each step):
- Build: `dotnet build -c Release` the test project
- Loop 100 times:
  - Run `dotnet test` with `--output detailed` and `--timeout 10s`
  - Run test process in background; check every 5 seconds
  - Normal runs finish in ~1-2 seconds
  - If still running after 5 seconds: **HANG detected** -- kill, mark as BAD, stop immediately
  - If test exits non-zero for other reasons (compile error, test failure): log and continue
- If all 100 pass: mark as GOOD

**Detection logic**: since a normal run completes in ~1-2s, a process alive at 5s is a guaranteed hang. One hang = FAIL. No need to run remaining iterations.

**Output**: `--output detailed` prints each test as it finishes, so we can see exactly which tests completed before the hang.

### Phase 2: Backport the specific hanging test to find the true origin

Once Phase 1 identifies commit X as the first commit that hangs:

1. **Identify the exact test** that's hanging (from the detailed output)
2. **Take that test backwards** from commit X to earlier commits
3. At each earlier commit:
   - Copy just the one hanging test file
   - Adapt it mechanically for API differences (renames, signatures)
   - Run it 100x
   - If it hangs: the bug was already present, keep going backwards
   - If it passes: the bug was introduced between this commit and the next
4. This narrows the true origin, which may be different from where it was first *detected*

The backporting effort is minimal because we're only porting a single test (or small set), not the entire test suite.

### Phase 3: Confirm and analyze

- Run the commit just before the bug 3x100 to confirm it's truly clean
- Diff the culprit PR to find the exact race condition
- The race is likely in `BoundedLocalQueue`, `WorkerPool` steal loop, or `InjectionQueue`

## PR History (bisection range)

```
5cf9915 Opt 8: Eliminate per-node zero-fill (#197)         <-- HANGS
2ea78fa Shrink ExecuteJob (#196)
baee1bc Opt 4: Batch IncrementOutstanding (#193)
61a4417 Add stress tests for PropagateCompletion (#194)
e0999b8 Gate unsafe behind UNSAFE_OPT (#195)
39cd2ef Flatten RemapTable storage (#192)
fed0900 Convert UnsafeList to mutable struct (#190)
ae1d61e Add optimizations plan (#189)
c7ad070 Convert RefCountTable to readonly struct (#187)
b9dad0a Remove WorkerCountOverride param (#188)
ce86d07 Batch InjectionQueue.Enqueue (#186)
4b30c80 Add PGO Tier1 optimization roadmap (#185)
8cd2ef4 Bounds-checked array access in Debug (#184)
0bf2f99 Replace integer division with conditional subtract (#183)
0534f1c Eliminate bounds checks in ComputeJob/SnapshotJob (#182)
2f934d8 Add JIT disassembly harness (#181)
570c1c2 Add plans for Apr 8-9 (#180)
dbbc926 Convert BLQ from class to struct (#179)
663ae9a Eliminate GC write barrier in hot-path (#178)
fa449e2 Replace per-element steal with bulk Array.Copy (#177)
5b3ea8e Add scheduler instrumentation (#176)
5e4d04b Wire BLQ into WorkerPool (#175)
0a340a1 Use InjectionQueue for WorkerPool (#174)
a748bfe Replace ConcurrentQueue with InjectionQueue (#173)
7cf2ec3 Align BLQ with Tokio (#172)
a1f300d Fix speculative-read-before-CAS race (#171)
324d005 Add BoundedLocalQueue core implementation (#169)
5fa8bbf Plan: Tokio-style fixed-capacity local queue (#168)  <-- likely CLEAN
```

## Expected Outcome

Most likely culprit range: PRs #169-#179 (BLQ introduction and iterations).
