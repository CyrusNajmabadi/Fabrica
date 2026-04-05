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
/// BASELINE DIRECTORY STRUCTURE
///   tests/baselines/jit/{os}-{arch}-net{major}.{minor}.{patch}/{method}.asm
///
///   The runtime version is included because even patch releases can change JIT codegen.
///   Old version directories are kept — they remain valid for anyone running that version.
///
/// WORKFLOW
///   1. If a baseline exists for the current platform+version, the test compares against it.
///   2. If no baseline exists, the test writes the current output as a <c>.actual</c> file next to
///      where the baseline would be, and fails with instructions to review and commit.
///   3. When the runtime is upgraded, the test will fail on first run (new version directory).
///      Do NOT delete old baselines — they're still valid for that version. Instead, review the
///      new <c>.actual</c> output and rename to <c>.asm</c> to add the new version's baseline.
/// </summary>
public partial class JitBaselineTests
{
    private static readonly string s_repoRoot = FindRepoRoot();
    private static readonly string s_baselineDir = Path.Combine(s_repoRoot, "tests", "baselines", "jit", PlatformKey());
    private static readonly string s_jitBaselineProject = Path.Combine(s_repoRoot, "tests", "Fabrica.JitBaseline", "Fabrica.JitBaseline.csproj");

    [Theory]
    [InlineData("VisitSameType", "visit-same-type")]
    [InlineData("VisitDifferentType", "visit-different-type")]
    [InlineData("EnumerateDecrement", "enumerate-decrement-same-type")]
    [InlineData("EnumerateMixedDecrement", "enumerate-decrement-mixed-type")]
    public void JitOutput_MatchesBaseline(string methodFilter, string baselineName)
    {
        var rawAsm = CaptureJitDisasm(methodFilter);
        Assert.True(rawAsm.Length > 0, $"DOTNET_JitDisasm produced no output for filter '*{methodFilter}*'. " +
            "This may mean the method was not JIT-compiled or the filter didn't match.");

        var normalized = NormalizeAsm(rawAsm);
        var baselineFile = Path.Combine(s_baselineDir, $"{baselineName}.asm");
        var actualFile = Path.Combine(s_baselineDir, $"{baselineName}.actual");

        if (!File.Exists(baselineFile))
        {
            Directory.CreateDirectory(s_baselineDir);
            File.WriteAllText(actualFile, normalized);
            Assert.Fail(
                $"No baseline found for {PlatformKey()}/{baselineName}.\n" +
                $"Generated output written to: {actualFile}\n" +
                "Review the output, and if correct, rename .actual → .asm to establish the baseline.");
            return;
        }

        var expected = File.ReadAllText(baselineFile);
        if (normalized != expected)
        {
            Directory.CreateDirectory(s_baselineDir);
            File.WriteAllText(actualFile, normalized);
            Assert.Fail(
                $"JIT output for {baselineName} on {PlatformKey()} differs from baseline.\n" +
                $"Baseline: {baselineFile}\n" +
                $"Actual:   {actualFile}\n" +
                "Review the diff. If the new output is correct, replace the .asm baseline.\n" +
                "Do NOT delete baselines for other platform/version directories — they remain valid.");
        }
        else if (File.Exists(actualFile))
        {
            File.Delete(actualFile);
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
    /// (1) absolute address immediates in <c>movz/movk</c> (ARM64) and <c>mov</c> (x64),
    /// (2) local function ordinals in method names (e.g., <c>|0_2</c> → <c>|0_N</c>),
    ///     which shift when helper functions are added/removed.
    /// Instruction structure, registers, and code flow are preserved.
    /// </summary>
    private static string NormalizeAsm(string asm)
    {
        // Local function ordinals: g__MethodName|0_2 → g__MethodName|0_N
        var result = LocalFunctionOrdinalPattern().Replace(asm, "${prefix}|0_N${suffix}");

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

    [GeneratedRegex(@"(?<prefix>g__\w+)(\|0_\d+)(?<suffix>\()")]
    private static partial Regex LocalFunctionOrdinalPattern();

    [GeneratedRegex(@"#0x(?<hex>[0-9A-Fa-f]+)")]
    private static partial Regex MovzMovkAddressPattern();

    [GeneratedRegex(@"\b0x(?<hex>[0-9A-Fa-f]{5,})\b")]
    private static partial Regex MovAbsolutePattern();
}
