using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Guards that the Anvil lab and Acta Bench publish with NativeAOT: <c>dotnet publish</c> of each must exit 0,
/// which catches a trim/AOT regression that breaks native compilation. The published bench then runs one
/// SQLite checkpoint case and must report native compilation in its evidence.
/// Gated behind <c>ACTA_AOT_PUBLISH_TEST</c> because the publish needs the ILCompiler toolchain and a
/// native linker and takes minutes; CI sets the variable on a runner that has the toolchain.
/// </summary>
public sealed class NativeAotPublishTests
{
    [Theory]
    [InlineData("anvil/Anvil/Anvil.csproj", "Anvil", false)]
    [InlineData("tools/Acta.Bench/Acta.Bench.csproj", "acta-bench", true)]
    public async Task Executables_publish_with_native_aot(string projectPath, string executableName, bool runCheckpointSmoke)
    {
        if (Environment.GetEnvironmentVariable("ACTA_AOT_PUBLISH_TEST") is null)
        {
            Assert.Skip("Set ACTA_AOT_PUBLISH_TEST=1 (with the NativeAOT toolchain installed) to run the publish guardrail.");
        }

        var repoRoot = ResolveRepoRoot();
        var project = Path.Combine(repoRoot, projectPath);
        var publishDirectory = Path.Combine(repoRoot, "artifacts", "aot-guard", executableName, Guid.NewGuid().ToString("N"));
        var rid = RuntimeInformation.RuntimeIdentifier;
        try
        {
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
            // PublishAot comes from each project file, not from here: a global -p:PublishAot=true would also
            // reach the netstandard source generator, which cannot publish AOT.
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(publishDirectory);
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
            if (!runCheckpointSmoke)
            {
                return;
            }

            var executable = Path.Combine(publishDirectory, executableName + (OperatingSystem.IsWindows() ? ".exe" : ""));
            var smokeInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (
                var argument in new[]
                {
                    "--quick",
                    "--db",
                    "sqlite",
                    "--case",
                    "C02",
                    "--output",
                    Path.Combine(publishDirectory, "smoke-evidence"),
                }
            )
            {
                smokeInfo.ArgumentList.Add(argument);
            }
            using var smoke = Process.Start(smokeInfo)!;
            var smokeOutput = smoke.StandardOutput.ReadToEndAsync(ct);
            var smokeError = smoke.StandardError.ReadToEndAsync(ct);
            try
            {
                await smoke.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(5), ct);
            }
            finally
            {
                if (!smoke.HasExited)
                {
                    smoke.Kill(entireProcessTree: true);
                    await smoke.WaitForExitAsync(CancellationToken.None);
                }
            }
            Assert.True(smoke.ExitCode == 0, $"Native checkpoint smoke failed: {await smokeOutput}\n{await smokeError}");
            var environmentFile = Directory
                .GetFiles(Path.Combine(publishDirectory, "smoke-evidence"), "environment.json", SearchOption.AllDirectories)
                .Single();
            using var environment = JsonDocument.Parse(await File.ReadAllTextAsync(environmentFile, ct));
            Assert.Equal("native_aot", environment.RootElement.GetProperty("compilation_mode").GetString());
        }
        finally
        {
            if (Directory.Exists(publishDirectory))
            {
                Directory.Delete(publishDirectory, recursive: true);
            }
        }
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
