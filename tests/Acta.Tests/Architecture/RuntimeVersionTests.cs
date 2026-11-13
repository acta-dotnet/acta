using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// The suite runs on the runtime its run names: the major of the target framework, or the one in
/// <c>ACTA_TEST_DOTNET_MAJOR</c> when a run rolls the test hosts forward on purpose. A host that rolled forward by
/// accident, or a roll-forward run that stayed behind, would otherwise pass for a runtime it never loaded.
/// </summary>
public sealed class RuntimeVersionTests
{
    [Fact]
    public void The_suite_runs_on_the_runtime_major_its_run_names()
    {
        var target = typeof(RuntimeVersionTests).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!;
        var expected = Environment.GetEnvironmentVariable("ACTA_TEST_DOTNET_MAJOR")
            ?? new FrameworkName(target.FrameworkName).Version.Major.ToString(CultureInfo.InvariantCulture);

        Assert.Equal(expected, Environment.Version.Major.ToString(CultureInfo.InvariantCulture));
    }
}
