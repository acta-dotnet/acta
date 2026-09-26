using Xunit;

namespace Acta.Tests.Generators;

/// <summary>
/// <c>[Job(Lane = ...)]</c> is checked at compile time under the concurrency-key rule, flows into the
/// generated descriptor in canonical lowercase, and cannot ride a scheduled definition.
/// </summary>
public sealed class LaneGeneratorTests
{
    public static TheoryData<string> InvalidLanes =>
        ["\"\"", "\"   \"", "\"has space\"", "\"caf\\u00e9\"", "\"sys.reserved\"", $"\"{new string('l', 129)}\""];

    [Theory]
    [MemberData(nameof(InvalidLanes))]
    public void Invalid_lane_errors_ACTA0105(string literal)
    {
        var result = ManifestGeneratorDiagnosticTests.RunGenerator(
            $$"""
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("laned", Lane = {{literal}})]
                public static void Run() { }
            }
            """
        );

        Assert.Single(result.Diagnostics, d => d.Id == "ACTA0105");
    }

    [Fact]
    public void Valid_lane_is_emitted_in_canonical_form()
    {
        var result = ManifestGeneratorDiagnosticTests.RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("laned", Lane = " Orders:42 ")]
                public static void Run() { }
            }
            """
        );

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "ACTA0105");
        Assert.Contains("Lane = \"orders:42\",", Assert.Single(result.GeneratedTrees).ToString());
    }

    [Fact]
    public void Laned_definition_with_a_schedule_errors_ACTA0121()
    {
        var result = ManifestGeneratorDiagnosticTests.RunGenerator(
            """
            using Acta;
            namespace GenTests;

            public static class Handler
            {
                [Job("laned-tick", Lane = "orders")]
                [JobSchedule("tick", Cron.Hourly)]
                public static void Run() { }
            }
            """
        );

        Assert.Single(result.Diagnostics, d => d.Id == "ACTA0121");
    }
}
