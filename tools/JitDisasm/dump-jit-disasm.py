#!/usr/bin/env python3
"""
Captures JIT disassembly for all scheduling hot-path methods in Fabrica.

Usage:
    python3 tools/JitDisasm/dump-jit-disasm.py [output_file]

Builds the JitDisasm harness in Release, then runs it once per method with
DOTNET_TieredCompilation=0 and DOTNET_JitDisasm set to each method name.
Methods that are inlined (no standalone disasm) are reported but skipped.

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


def capture(method: str) -> list[str]:
    env = os.environ.copy()
    env["DOTNET_TieredCompilation"] = "0"
    env["DOTNET_JitDisasm"] = method

    result = subprocess.run(
        ["dotnet", DLL],
        capture_output=True, text=True, env=env, timeout=30,
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


def main():
    output_path = sys.argv[1] if len(sys.argv) > 1 else None

    build()

    all_lines: list[str] = []

    for method in METHODS:
        asm = capture(method)
        if asm:
            all_lines.extend(asm)
            all_lines.append("")
            print(f"  {method}: {len(asm)} lines")
        else:
            print(f"  {method}: (inlined, no standalone disasm)")

    text = "\n".join(all_lines) + "\n"

    if output_path:
        os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
        with open(output_path, "w") as f:
            f.write(text)
        print(f"\nWrote {len(all_lines)} lines to {output_path}")
    else:
        print(text)


if __name__ == "__main__":
    main()
