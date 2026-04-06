using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Fabrica.Core.Tests.Memory;

/// <summary>
/// Golden-file tests that verify the JIT produces the expected native code for critical visitor methods.
/// Uses <c>DOTNET_JitDisasm</c> to dump the JIT output from the <c>Fabrica.JitBaseline</c> helper app,
/// normalizes volatile values (absolute addresses), and compares against checked-in baseline files.
///
/// <para><b>THREE LAYERS OF VERIFICATION</b></para>
/// <list type="number">
///   <item><b>Runtime correctness</b>: The <c>Fabrica.JitBaseline</c> app asserts that refcounts are
///     actually updated correctly (exit code 1 on failure). This catches functional regressions
///     regardless of what the ASM looks like.</item>
///   <item><b>Structural validation</b>: After capturing the ASM, we verify that <c>typeof</c> checks
///     were eliminated by the JIT. See <see cref="AssertTypeofEliminated"/> for details.</item>
///   <item><b>Baseline comparison</b>: The exact normalized ASM is compared against a golden file.
///     This catches any codegen regression, even ones that are still "correct" but suboptimal.</item>
/// </list>
///
/// <para><b>HOW WE VERIFY typeof ELIMINATION</b></para>
/// <para>
/// When the JIT constant-folds <c>typeof(T) == typeof(SomeType)</c> in a generic struct method,
/// it emits only the surviving branch body (or nothing at all for false branches). If the check were
/// NOT eliminated, the ASM would contain one of:
/// <list type="bullet">
///   <item><c>GetTypeFromHandle</c> / <c>RuntimeTypeHandle</c> calls to materialize Type objects</item>
///   <item>A MethodTable pointer comparison (load 64-bit address + <c>cmp</c>)</item>
///   <item><c>CORINFO_HELP</c> runtime helper calls for type checks</item>
/// </list>
/// We assert none of these patterns appear (negative check), and also count the number of
/// refcount decrements (<c>sub wN, wN, #1</c> on ARM64) to confirm exactly the right number
/// of Decrement calls were inlined (positive check).
/// </para>
///
/// <para><b>EXPECTED DECREMENT COUNTS PER SCENARIO</b></para>
/// <list type="table">
///   <item><term>visit-same-type (1)</term>
///     <description><c>typeof(TreeNode)==typeof(TreeNode)</c> → true → direct Decrement</description></item>
///   <item><term>visit-different-type (0)</term>
///     <description><c>typeof(OtherNode)==typeof(TreeNode)</c> → false → entire body eliminated</description></item>
///   <item><term>enumerate-decrement-same-type (2)</term>
///     <description>Both children are <c>TreeNode</c> → 2 Decrements inlined</description></item>
///   <item><term>enumerate-decrement-mixed-type (1)</term>
///     <description><c>MixedDecrementVisitor</c> handles only <c>MixedNode</c>;
///       the <c>OtherNode</c> branch is dead-code eliminated → 1 Decrement</description></item>
///   <item><term>enumerate-decrement-multi-type (2)</term>
///     <description><c>ParentDecrementVisitor</c> has two live typeof branches (ParentNode + ChildNode);
///       both resolve → 2 Decrements from two different tables</description></item>
/// </list>
///
/// <para><b>BASELINE DIRECTORY STRUCTURE</b></para>
/// <para>
///   <c>tests/baselines/jit/{os}-{arch}-net{major}.{minor}.{patch}/{method}.asm</c>
/// </para>
/// <para>
///   The runtime version is included because even patch releases can change JIT codegen.
///   Old version directories are kept — they remain valid for anyone running that version.
/// </para>
///
/// <para><b>WORKFLOW</b></para>
/// <list type="number">
///   <item>If a baseline exists for the current platform+version, the test compares against it.</item>
///   <item>If no baseline exists, the test writes the current output as a <c>.actual</c> file next to
///     where the baseline would be, and fails with instructions to review and commit.</item>
///   <item>When the runtime is upgraded, the test will fail on first run (new version directory).
///     Do NOT delete old baselines — they're still valid for that version. Instead, review the
///     new <c>.actual</c> output and rename to <c>.asm</c> to add the new version's baseline.
///     If the output is identical to an existing version, create a <c>.ref</c> file instead
///     (a text file containing the relative path to the matching <c>.asm</c>).</item>
/// </list>
/// </summary>
public partial class JitBaselineTests
{
    private static readonly string s_repoRoot = FindRepoRoot();
    private static readonly string s_baselineDir = Path.Combine(s_repoRoot, "tests", "baselines", "jit", PlatformKey());
    private static readonly string s_jitBaselineProject = Path.Combine(s_repoRoot, "tests", "Fabrica.JitBaseline", "Fabrica.JitBaseline.csproj");

    [Theory]
    [InlineData("VisitSameType", "visit-same-type", 1)]
    [InlineData("VisitDifferentType", "visit-different-type", 0)]
    [InlineData("EnumerateDecrement", "enumerate-decrement-same-type", 2)]
    [InlineData("EnumerateMixedDecrement", "enumerate-decrement-mixed-type", 1)]
    [InlineData("EnumerateParentDecrement", "enumerate-decrement-multi-type", 2)]
    public void JitOutput_MatchesBaseline(string methodFilter, string baselineName, int expectedDecrements) => this.VerifyBaseline(methodFilter, baselineName, expectedDecrements, requireOutput: true);

    /// <summary>
    /// Callee-level baselines: capture the methods the wrappers call into, where typeof elimination
    /// actually happens on architectures that don't inline into <c>NoInlining</c> wrappers (e.g., x64).
    /// On ARM64, these callees are fully inlined and never compiled standalone — the test skips.
    /// </summary>
    [Theory]
    [InlineData("TreeChildEnumerator:EnumerateChildren", "enumerate-decrement-same-type-callee", 2)]
    [InlineData("MixedChildEnumerator:EnumerateChildren", "enumerate-decrement-mixed-type-callee", 1)]
    [InlineData("ParentChildEnumerator:EnumerateChildren", "enumerate-decrement-multi-type-callee", 2)]
    public void JitOutput_CalleeMatchesBaseline(string methodFilter, string baselineName, int expectedDecrements) => this.VerifyBaseline(methodFilter, baselineName, expectedDecrements, requireOutput: false);

    private void VerifyBaseline(string methodFilter, string baselineName, int expectedDecrements, bool requireOutput)
    {
#if DEBUG
        Assert.Skip("JIT baseline tests require Release configuration — Debug JIT does not inline or eliminate typeof branches.");
        return;
#endif

#pragma warning disable CS0162 // Unreachable code (the return above is conditional on DEBUG)
        var rawAsm = CaptureJitDisasm(methodFilter);

        if (rawAsm.Length == 0)
        {
            if (requireOutput)
            {
                Assert.Fail($"DOTNET_JitDisasm produced no output for filter '*{methodFilter}*'. " +
                    "This may mean the method was not JIT-compiled or the filter didn't match.");
            }

            // Callee was fully inlined — the wrapper baseline contains the optimization proof.
            Assert.Skip($"No standalone compilation for '*{methodFilter}*' — method was fully inlined by the JIT.");
            return;
        }

        var normalized = NormalizeAsm(rawAsm);

        AssertTypeofEliminated(normalized, baselineName, expectedDecrements);

        var actualFile = Path.Combine(s_baselineDir, $"{baselineName}.actual");
        var resolvedBaseline = ResolveBaseline(s_baselineDir, baselineName);

        if (resolvedBaseline is null)
        {
            Directory.CreateDirectory(s_baselineDir);
            File.WriteAllText(actualFile, normalized);

            var matchHint = FindMatchingBaseline(baselineName, normalized);
            var message = $"No baseline found for {PlatformKey()}/{baselineName}.\n" +
                $"Generated output written to: {actualFile}\n";
            message += matchHint is not null
                ? $"Output MATCHES existing baseline: {matchHint}\n" +
                  $"To deduplicate, create a .ref file instead of a new .asm:\n" +
                  $"  echo \"{matchHint}\" > {Path.Combine(s_baselineDir, $"{baselineName}.ref")}\n"
                : "Review the output, and if correct, add it as the .asm baseline.\n";
            message += $"\n--- BEGIN {baselineName}.asm ---\n{normalized}--- END {baselineName}.asm ---";

            Assert.Fail(message);
            return;
        }

        var expected = File.ReadAllText(resolvedBaseline);
        if (normalized != expected)
        {
            Directory.CreateDirectory(s_baselineDir);
            File.WriteAllText(actualFile, normalized);
            Assert.Fail(
                $"JIT output for {baselineName} on {PlatformKey()} differs from baseline.\n" +
                $"Baseline: {resolvedBaseline}\n" +
                $"Actual:   {actualFile}\n" +
                "Review the diff. If the new output is correct, replace the .asm baseline.\n" +
                "Do NOT delete baselines for other platform/version directories — they remain valid.\n" +
                $"\n--- BEGIN {baselineName}.asm ---\n{normalized}--- END {baselineName}.asm ---");
        }
        else if (File.Exists(actualFile))
        {
            File.Delete(actualFile);
        }
#pragma warning restore CS0162
    }

    /// <summary>
    /// Validates that <c>typeof()</c> checks were eliminated by the JIT via two independent checks:
    ///
    /// <para><b>Negative check</b>: Scans for patterns that would ONLY appear if <c>typeof</c> were
    /// evaluated at runtime. If the JIT fails to constant-fold <c>typeof(T1) == typeof(T2)</c>,
    /// it must materialize <c>System.Type</c> objects or compare MethodTable pointers, which
    /// produces recognizable strings in the disassembly:</para>
    /// <list type="bullet">
    ///   <item><c>GetTypeFromHandle</c> — runtime call to create a Type from a RuntimeTypeHandle</item>
    ///   <item><c>RuntimeTypeHandle</c> — the handle struct itself appearing as a parameter</item>
    ///   <item><c>CORINFO_HELP</c> — JIT helper calls for type operations</item>
    /// </list>
    ///
    /// <para><b>Positive check</b>: Counts occurrences of the architecture-specific refcount
    /// decrement instruction. Each fully-inlined <see cref="Core.Memory.RefCountTable{T}.Decrement"/>
    /// call produces exactly one. The expected count directly reflects which typeof branches
    /// survived constant-folding. For example, <c>visit-different-type</c> expects 0 (the typeof was
    /// false, so the entire body was eliminated), while <c>enumerate-decrement-multi-type</c> expects 2
    /// (both typeof branches are true for their respective types).</para>
    /// <para>Architecture-specific patterns:</para>
    /// <list type="bullet">
    ///   <item>ARM64: <c>sub wN, wN, #1</c></item>
    ///   <item>x64: <c>dec dword ptr [reg+offset]</c> or <c>add dword ptr [reg+offset], -1</c></item>
    /// </list>
    /// </summary>
    private static void AssertTypeofEliminated(string asm, string scenarioName, int expectedDecrements)
    {
        // NEGATIVE CHECK: these strings only appear if the JIT failed to constant-fold a typeof
        // comparison and fell back to runtime type resolution.
        string[] typeofIndicators = ["GetTypeFromHandle", "RuntimeTypeHandle", "CORINFO_HELP"];
        foreach (var indicator in typeofIndicators)
        {
            Assert.DoesNotContain(indicator, asm, StringComparison.OrdinalIgnoreCase);
        }

        // POSITIVE CHECK: count refcount decrements. Each inlined RefCountTable.Decrement produces
        // exactly one of these. The count tells us which typeof branches survived.
        //
        // On x64, wrapper methods may be thin call stubs (the JIT doesn't inline into NoInlining
        // wrappers). The callee baselines contain the actual inlined code. We detect thin wrappers
        // by checking for call instructions to callee methods — skip the positive check for those.
        var arch = RuntimeInformation.OSArchitecture;
        var isThinWrapper = arch == Architecture.X64 && ThinCallPattern().IsMatch(asm);

        if (!isThinWrapper)
        {
            int? actualDecrements = arch switch
            {
                Architecture.Arm64 => Arm64DecrementPattern().Matches(asm).Count,
                Architecture.X64 => X64DecrementPattern().Matches(asm).Count,
                _ => null,
            };

            if (actualDecrements is not null)
            {
                var patternDesc = arch == Architecture.Arm64
                    ? "ARM64: 'sub wN, wN, #1'"
                    : "x64: 'dec dword ptr' or 'add dword ptr [...], -1'";
                Assert.True(actualDecrements == expectedDecrements,
                    $"[{scenarioName}] Expected {expectedDecrements} refcount decrement(s) ({patternDesc}) " +
                    $"but found {actualDecrements}. This indicates the JIT may not have eliminated typeof checks or " +
                    "failed to inline Decrement calls as expected.");
            }
        }
    }

    private static string CaptureJitDisasm(string methodFilter)
    {
        var dll = FindBuiltDll();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { dll },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["DOTNET_JitDisasm"] = $"*{methodFilter}*";
        psi.Environment["DOTNET_TieredCompilation"] = "0";

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(TimeSpan.FromSeconds(30));
        Assert.True(proc.ExitCode == 0,
            $"JitBaseline app exited with code {proc.ExitCode}. " +
            $"This means a runtime correctness check failed.\nstderr: {stderr}");
        return stdout;
    }

    /// <summary>
    /// Normalizes JIT disasm output by replacing volatile values with stable placeholders:
    /// (1) ISA extension lists in the header (e.g., <c>VEX + EVEX</c> varies by CPU),
    /// (2) absolute address immediates in <c>movz/movk</c> (ARM64) and <c>mov</c> (x64),
    /// (3) local function ordinals in method names (e.g., <c>|0_2</c> → <c>|0_N</c>),
    ///     which shift when helper functions are added/removed.
    /// Instruction structure, registers, and code flow are preserved.
    /// </summary>
    private static string NormalizeAsm(string asm)
    {
        // ISA extensions vary by hardware (e.g., "VEX" vs "VEX + EVEX" depending on AVX-512 support)
        var result = IsaExtensionPattern().Replace(asm, "; Emitting BLENDED_CODE for generic ${arch} on ${os}");

        // Local function ordinals: g__MethodName|0_2 → g__MethodName|0_N
        result = LocalFunctionOrdinalPattern().Replace(result, "${prefix}|0_N${suffix}");

        // ARM64: movz/movk with hex immediates for address loading
        result = MovzMovkAddressPattern().Replace(result, match =>
        {
            var hex = match.Groups["hex"].Value;
            if (hex.Length <= 2)
                return match.Value;
            return match.Value.Replace($"#0x{hex}", "#<addr>");
        });

        // x64: mov with large hex immediates (absolute addresses)
        result = MovAbsolutePattern().Replace(result, match =>
        {
            var hex = match.Groups["hex"].Value;
            if (hex.Length <= 4)
                return match.Value;
            return match.Value.Replace($"0x{hex}", "<addr>");
        });

        return result;
    }

    private static string FindBuiltDll()
    {
        var projectDir = Path.GetDirectoryName(s_jitBaselineProject)!;
        var dll = Path.Combine(projectDir, "bin", "Release", "net10.0", "Fabrica.JitBaseline.dll");
        if (!File.Exists(dll))
        {
            var result = RunProcess("dotnet", ["build", s_jitBaselineProject, "-c", "Release", "--no-restore"]);
            Assert.True(File.Exists(dll), $"Build succeeded but DLL not found at {dll}.\nBuild output: {result}");
        }

        return dll;
    }

    private static string RunProcess(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit(TimeSpan.FromSeconds(60));
        return output + error;
    }

    private static string PlatformKey()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : "unknown";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => "unknown",
        };
        var version = Environment.Version;
        return $"{os}-{arch}-net{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// Resolves a baseline for the given name. Checks for <c>.asm</c> first, then <c>.ref</c>.
    /// A <c>.ref</c> file contains a relative path to the actual <c>.asm</c> baseline,
    /// enabling deduplication across runtime versions with identical JIT output.
    /// </summary>
    private static string? ResolveBaseline(string baselineDir, string baselineName)
    {
        var asmFile = Path.Combine(baselineDir, $"{baselineName}.asm");
        if (File.Exists(asmFile))
            return asmFile;

        var refFile = Path.Combine(baselineDir, $"{baselineName}.ref");
        if (File.Exists(refFile))
        {
            var relativePath = File.ReadAllText(refFile).Trim();
            var resolved = Path.GetFullPath(Path.Combine(baselineDir, relativePath));
            Assert.True(File.Exists(resolved),
                $"Baseline .ref file {refFile} points to {relativePath}, but resolved path {resolved} does not exist.");
            return resolved;
        }

        return null;
    }

    /// <summary>
    /// Scans sibling version directories (same os-arch prefix) for an existing <c>.asm</c> baseline
    /// with identical content. Returns a relative path suitable for a <c>.ref</c> file, or null.
    /// </summary>
    private static string? FindMatchingBaseline(string baselineName, string normalizedAsm)
    {
        var jitDir = Path.GetDirectoryName(s_baselineDir)!;
        var currentDirName = Path.GetFileName(s_baselineDir);
        var platformPrefix = OsArchPrefix();

        foreach (var siblingDir in Directory.GetDirectories(jitDir))
        {
            var siblingDirName = Path.GetFileName(siblingDir);
            if (siblingDirName == currentDirName)
                continue;
            if (!siblingDirName.StartsWith(platformPrefix, StringComparison.Ordinal))
                continue;

            var candidateAsm = Path.Combine(siblingDir, $"{baselineName}.asm");
            if (!File.Exists(candidateAsm))
                continue;

            if (File.ReadAllText(candidateAsm) == normalizedAsm)
                return $"../{siblingDirName}/{baselineName}.asm";
        }

        return null;
    }

    private static string OsArchPrefix()
    {
        var key = PlatformKey();
        var lastDash = key.LastIndexOf("-net", StringComparison.Ordinal);
        return lastDash >= 0 ? key[..lastDash] : key;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find repository root (.git directory) from " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"; Emitting BLENDED_CODE for generic (?<arch>\w+)[\w\s+]*on (?<os>\w+)")]
    private static partial Regex IsaExtensionPattern();

    [GeneratedRegex(@"(?<prefix>g__\w+)(\|0_\d+)(?<suffix>\()")]
    private static partial Regex LocalFunctionOrdinalPattern();

    [GeneratedRegex(@"#0x(?<hex>[0-9A-Fa-f]+)")]
    private static partial Regex MovzMovkAddressPattern();

    [GeneratedRegex(@"\b0x(?<hex>[0-9A-Fa-f]{5,})\b")]
    private static partial Regex MovAbsolutePattern();

    /// <summary>
    /// Matches the ARM64 refcount decrement: <c>sub wN, wN, #1</c>.
    /// Each fully-inlined <c>RefCountTable.Decrement</c> produces exactly one of these.
    /// </summary>
    [GeneratedRegex(@"sub\s+w\d+,\s*w\d+,\s*#1\b")]
    private static partial Regex Arm64DecrementPattern();

    /// <summary>
    /// Matches the x64 refcount decrement: <c>dec dword ptr [...]</c> or <c>add dword ptr [...], -1</c>.
    /// Each fully-inlined <c>RefCountTable.Decrement</c> produces exactly one of these.
    /// </summary>
    [GeneratedRegex(@"(dec\s+dword\s+ptr|add\s+dword\s+ptr\s+\[.+?\],\s*-1)")]
    private static partial Regex X64DecrementPattern();

    /// <summary>
    /// Detects x64 thin wrapper methods: the JIT doesn't inline into <c>NoInlining</c> wrappers,
    /// so the ASM is just <c>call [MethodName]</c>. Positive decrement checks are meaningless here.
    /// </summary>
    [GeneratedRegex(@"call\s+\[")]
    private static partial Regex ThinCallPattern();
}
