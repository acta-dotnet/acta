using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// A conformance test deploys its catalog into a namespace of its own, so a spec that took every test job would write
/// them all for each fact. A spec names the test jobs it deploys with <c>[Deploys]</c> and takes
/// <c>JobsOf&lt;TSpec&gt;</c> as its manifest. The whole test manifest is only ever referenced, which writes nothing.
/// </summary>
public sealed partial class SpecCatalogTests
{
    [Fact(DisplayName = "No conformance spec deploys the whole test manifest")]
    public void No_spec_deploys_the_whole_test_manifest()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var violations = Directory
            .EnumerateDirectories(Path.Combine(root, "tests"), "Acta.Tests.Conformance*")
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(file =>
                !file.Relative.Contains("/obj/", StringComparison.Ordinal) && !file.Relative.Contains("/bin/", StringComparison.Ordinal)
            )
            .SelectMany(file => File.ReadLines(file.Path).Select((text, index) => (file.Relative, Line: index + 1, Text: text)))
            .Where(line => WholeManifest().IsMatch(line.Text))
            .Select(line => $"{line.Relative}:{line.Line}");

        ArchitectureAssert.NoViolations(
            violations,
            "Name the jobs the spec deploys with [Deploys(...)] and take JobsOf<TSpec> as its manifest."
        );
    }

    // The test manifest as a type argument anywhere but a Reference, or its descriptors handed on whole.
    [GeneratedRegex(@"(?<!Reference)(?:<|,\s*)(?:TestJobs\.)?TestJobsManifest\s*>|TestJobsManifest\.Descriptors\.Descriptors\s*[,)]")]
    private static partial Regex WholeManifest();
}
