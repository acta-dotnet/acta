using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Every operation has a conformance spec a reader finds by its name, and every engine area has specs beside it:
/// <c>Operations/Group/OperationSpec.cs</c> (or <c>OperationFacetSpec.cs</c>) and <c>Engine/Area/</c> under
/// <c>tests/Acta.Tests.Conformance</c>, mirroring <c>src/Acta</c>.
/// </summary>
public sealed class SpecCoverageTests
{
    [Fact]
    public void Every_operation_and_engine_area_has_a_spec()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var runtime = Path.Combine(root, "src", "Acta");
        var specs = Path.Combine(root, "tests", "Acta.Tests.Conformance");
        var missing = new List<string>();

        foreach (var group in Directory.EnumerateDirectories(Path.Combine(runtime, "Operations")).Select(Path.GetFileName).OfType<string>())
        {
            var names = SpecNames(Path.Combine(specs, "Operations", group));
            foreach (
                var operation in Directory
                    .EnumerateDirectories(Path.Combine(runtime, "Operations", group))
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Where(name => name != "Shared")
            )
            {
                if (!names.Any(name => IsSpecOf(name, operation)))
                {
                    missing.Add($"operation: {group}/{operation}");
                }
            }
        }

        foreach (var area in Directory.EnumerateDirectories(Path.Combine(runtime, "Engine")).Select(Path.GetFileName).OfType<string>())
        {
            if (SpecNames(Path.Combine(specs, "Engine", area)).Count == 0)
            {
                missing.Add($"engine: {area}");
            }
        }

        ArchitectureAssert.NoViolations(
            missing,
            "Add a spec named after the operation (Operations/<Group>/<Operation>Spec.cs), or one under Engine/<Area>/."
        );
    }

    private static List<string> SpecNames(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*Spec.cs").Select(Path.GetFileNameWithoutExtension).OfType<string>()]
            : [];

    // PauseJobSpec and PauseJobFilterMatrixSpec specify PauseJob; PauseJobsSpec does not.
    private static bool IsSpecOf(string name, string operation) =>
        name.StartsWith(operation, StringComparison.Ordinal) && name.Length > operation.Length && char.IsUpper(name[operation.Length]);
}
