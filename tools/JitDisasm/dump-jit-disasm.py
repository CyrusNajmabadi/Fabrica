#!/usr/bin/env python3
"""
Captures JIT disassembly for all scheduling hot-path methods in Fabrica.

Usage:
    python3 tools/JitDisasm/dump-jit-disasm.py [output_dir]

Two passes:
  1. No-PGO (TieredCompilation=0) — one standalone FullOpts compilation per
     method, easy to read individually.  Saved as <output_dir>/no-pgo.txt.
  2. PGO (tiered compilation ON, DOTNET_JitDisasmOnlyOptimized=1) — captures
     the Tier1-OSR compilations with synthesized PGO, matching what
     BenchmarkDotNet actually executes.  Most hot methods are inlined into the
     RunWorker OSR body.  Saved as <output_dir>/pgo.txt.

The harness runs 100 ticks to give the tiering system enough data to promote
hot methods.

Requires: dotnet CLI on PATH, the JitDisasm project to build successfully.
"""

import subprocess
import sys
import os

METHODS = [
    "TryPop",
    "BufferAt",
    "Push",
    "PushToRingBuffer",
    "TryStealHalf",
    "ExecuteJob",
    "PropagateCompletion",
    "TryStealAndExecute",
    "TryDequeueInjected",
    "TryExecuteOne",
    "TryWakeOneWorker",
    "TryWakeWorkers",
    "TransitionFromSearching",
    "RunUntilComplete",
    "RunWorker",
    "Enqueue",
    "Submit",
    "DecrementOutstanding",
    "IncrementOutstanding",
    "IncrementOutstandingBy",
    "Rent",
    "Return",
    "AddKnowingRefcountIsZero",
    "NextN",
    "Next",
]

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PROJECT = os.path.join(REPO_ROOT, "tools", "JitDisasm")
DLL = os.path.join(PROJECT, "bin", "Release", "net10.0", "JitDisasm.dll")


def build():
    print("Building JitDisasm harness (Release)...")
    result = subprocess.run(
        ["dotnet", "build", PROJECT, "-c", "Release", "--no-incremental"],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        print(result.stderr, file=sys.stderr)
        sys.exit(1)
    print("Build succeeded.\n")


def capture(method: str, pgo: bool) -> list[str]:
    env = os.environ.copy()

    if pgo:
        # Tiered compilation ON (default) + only optimized output.
        # Hot methods are inlined into RunWorker via Tier1-OSR.
        env["DOTNET_JitDisasmOnlyOptimized"] = "1"
    else:
        # Tiered compilation OFF: FullOpts without PGO, one compilation
        # per method (easier to read individual methods).
        env["DOTNET_TieredCompilation"] = "0"

    env["DOTNET_JitDisasm"] = method

    result = subprocess.run(
        ["dotnet", DLL],
        capture_output=True, text=True, env=env, timeout=60,
    )

    lines = result.stdout.strip().split("\n")
    asm_lines: list[str] = []
    capturing = False

    for line in lines:
        if "; Assembly listing for method" in line and "Fabrica." in line:
            capturing = True
            asm_lines.append(line)
        elif capturing:
            if line.startswith("; Assembly listing") and "Fabrica." not in line:
                capturing = False
            elif line == "JIT disasm harness complete.":
                capturing = False
            else:
                asm_lines.append(line)

    return asm_lines


PGO_METHODS = [
    "RunWorker",
    "RunUntilComplete",
    "PropagateCompletion",
    "Execute",
]


def main():
    output_dir = sys.argv[1] if len(sys.argv) > 1 else None

    build()

    # ── Pass 1: No-PGO (TieredCompilation=0), per-method ─────────────
    print("=== No-PGO pass (TieredCompilation=0, FullOpts) ===")
    nopgo_lines: list[str] = []

    for method in METHODS:
        asm = capture(method, pgo=False)
        if asm:
            nopgo_lines.extend(asm)
            nopgo_lines.append("")
            print(f"  {method}: {len(asm)} lines")
        else:
            print(f"  {method}: (inlined)")

    # ── Pass 2: PGO (tiered ON, only optimized) ──────────────────────
    print("\n=== PGO pass (Tier1-OSR with PGO) ===")
    pgo_lines: list[str] = []

    for method in PGO_METHODS:
        asm = capture(method, pgo=True)
        if asm:
            pgo_lines.extend(asm)
            pgo_lines.append("")
            headers = [l for l in asm if "; Assembly listing" in l]
            print(f"  {method}: {len(asm)} lines ({len(headers)} compilation(s))")
        else:
            print(f"  {method}: (no optimized disasm — inlined into caller)")

    # ── Write output ──────────────────────────────────────────────────
    if output_dir:
        os.makedirs(output_dir, exist_ok=True)

        nopgo_path = os.path.join(output_dir, "no-pgo.txt")
        with open(nopgo_path, "w") as f:
            f.write("\n".join(nopgo_lines) + "\n")
        print(f"\nWrote {len(nopgo_lines)} lines to {nopgo_path}")

        pgo_path = os.path.join(output_dir, "pgo.txt")
        with open(pgo_path, "w") as f:
            f.write("\n".join(pgo_lines) + "\n")
        print(f"Wrote {len(pgo_lines)} lines to {pgo_path}")
    else:
        print("\n=== No-PGO disasm ===")
        print("\n".join(nopgo_lines))
        print("\n=== PGO disasm ===")
        print("\n".join(pgo_lines))


if __name__ == "__main__":
    main()
