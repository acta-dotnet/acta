using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Acta.Tests.Aot;

/// <summary>
/// Guards that the real Anvil lab app publishes with NativeAOT: <c>dotnet publish</c> of <c>anvil/Anvil</c>
/// (the web host, both database providers, and the folded-in bench engine, with source-generated JSON for
/// every DTO) must complete with exit code 0. Catches a trim/AOT regression that breaks native
/// compilation; it does not assert runtime behavior (the run-smoke is a separate CI step).
/// Gated behind <c>ACTA_AOT_PUBLISH_TEST</c> because the publish needs the ILCompiler toolchain and a
/// native linker and takes minutes; CI sets the variable on a runner that has the toolchain.
/// </summary>
public sealed class NativeAotPublishTests
{
    [Fact]
    public async Task Anvil_publishes_with_native_aot()
    {
        if (Environment.GetEnvironmentVariable("ACTA_AOT_PUBLISH_TEST") is null)
        {
            Assert.Skip("Set ACTA_AOT_PUBLISH_TEST=1 (with the NativeAOT toolchain installed) to run the publish guardrail.");
        }

        var repoRoot = ResolveRepoRoot();
        var project = Path.Combine(repoRoot, "anvil", "Anvil", "Anvil.csproj");
        var rid = RuntimeInformation.RuntimeIdentifier;

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("publish");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(rid);
        // The dashboard is static-asset bundling, not AOT-relevant code; skip its npm/Vite build so the
        // guardrail checks native compilation of the C# graph without needing Node on the runner.
        psi.ArgumentList.Add("-p:ActaDashboardSkipNpm=true");
        psi.ArgumentList.Add("-nodeReuse:false");
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(psi)!;
        var ct = TestContext.Current.CancellationToken;
        // Read both redirected streams concurrently: a native publish can produce enough analyzer
        // output to fill one pipe while a synchronous read is waiting for EOF on the other.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = output[0];
        var stderr = output[1];

        Assert.True(
            process.ExitCode == 0,
            $"NativeAOT publish failed (exit {process.ExitCode}) for rid {rid}.\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}"
        );
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Acta.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("NativeAotPublishTests could not locate Acta.slnx from " + AppContext.BaseDirectory);
    }
}
