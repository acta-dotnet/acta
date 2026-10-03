using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds <c>src/</c> to the operation layout in <c>docs/internals/source-layout.md</c>: <c>Operations/Group/Operation/</c>
/// and no deeper, files named after their operation, all three dialects or none, and SQL only under <c>Operations/</c>
/// and <c>Schema/</c>. Once a group's Runtime folder holds C#, each of its operations needs a handler and each public
/// operation folder a Runtime twin. What still differs is listed in the <c>layout</c> ratchet.
/// </summary>
public sealed class OperationLayoutTests
{
    private static readonly string[] Dialects = ["pg", "mssql", "sqlite"];

    [Fact]
    public void Operations_follow_the_source_layout()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var runtime = Path.Combine(root, "src", "Acta.Runtime", "Operations");
        var contract = Path.Combine(root, "src", "Acta", "Operations");
        var violations = SqlPlacement().ToList();

        foreach (var operation in OperationFolders(runtime))
        {
            var landed = Directory.EnumerateFiles(Path.GetDirectoryName(operation)!, "*.cs", SearchOption.AllDirectories).Any();
            violations.AddRange(CheckOperation(root, operation, requireHandler: landed));
        }
        foreach (var operation in OperationFolders(contract))
        {
            violations.AddRange(CheckOperation(root, operation, requireHandler: false));
            var twin = Path.Combine(runtime, Path.GetRelativePath(contract, operation));
            var group = Path.GetDirectoryName(twin)!;
            if (
                Directory.Exists(group)
                && Directory.EnumerateFiles(group, "*.cs", SearchOption.AllDirectories).Any()
                && !Directory.Exists(twin)
            )
            {
                violations.Add($"mirror: {Relative(root, operation)}");
            }
        }

        Ratchet.Check("layout", violations, "Move the file to match docs/internals/source-layout.md.");
    }

    private static IEnumerable<string> OperationFolders(string operations) =>
        Directory.Exists(operations) ? Directory.EnumerateDirectories(operations).SelectMany(Directory.EnumerateDirectories) : [];

    private static IEnumerable<string> CheckOperation(string root, string folder, bool requireHandler)
    {
        var name = Path.GetFileName(folder);
        foreach (var nested in Directory.EnumerateDirectories(folder))
        {
            yield return $"depth: {Relative(root, nested)}";
        }

        var files = Directory.EnumerateFiles(folder).Select(Path.GetFileName).ToList();
        foreach (var file in files.Where(f => !f!.StartsWith(name, StringComparison.Ordinal)))
        {
            yield return $"file-name: {Relative(root, folder)}/{file}";
        }
        if (requireHandler && !files.Contains(name + "Handler.cs"))
        {
            yield return $"handler: {Relative(root, folder)}";
        }
        var dialects = Dialects.Count(d => files.Contains($"{name}.{d}.sql"));
        if (dialects is not 0 && dialects != Dialects.Length)
        {
            yield return $"dialects: {Relative(root, folder)}";
        }
    }

    // SQL lives beside its operation or in a project's Schema folder; anything else is keyed by its top folder.
    private static IEnumerable<string> SqlPlacement() =>
        ShippedCode
            .SourceFiles("*.sql")
            .Select(file => file.Relative.Split('/'))
            .Where(parts => parts.Length > 3 && parts[2] is not "Operations" and not "Schema")
            .Select(parts => $"sql-placement: {parts[0]}/{parts[1]}/{parts[2]}");

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
