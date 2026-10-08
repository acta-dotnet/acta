using Acta.Tests.Conformance.Testing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds <c>src/</c> to the operation layout in <c>docs/internals/source-layout.md</c>: <c>Operations/Group/Operation/</c>
/// and no deeper, files named after their operation, all three dialects or none, and SQL only under <c>Operations/</c>
/// and <c>Schema/</c>, every Schema stem in all three dialects, and no stem twice in one dialect. Each operation has a
/// handler, and no Operations file names a type declared under <c>Engine/</c>. What still differs is listed in the
/// <c>layout</c> ratchet.
/// </summary>
public sealed class OperationLayoutTests
{
    private static readonly string[] Dialects = ["pg", "mssql", "sqlite"];

    // SQLite has no DROP SCHEMA; its bootstrap clears sqlite_master objects in code instead.
    private static readonly string[] SingleDialectStems = ["DropSchema"];

    [Fact]
    public void Operations_follow_the_source_layout()
    {
        var root = IntegrationConfig.FindRepoRoot();
        var acta = Path.Combine(root, "src", "Acta");
        var violations = SqlPlacement().Concat(SqlStems(acta)).ToList();

        foreach (var operation in OperationFolders(Path.Combine(acta, "Operations")))
        {
            violations.AddRange(CheckOperation(root, operation));
        }

        Ratchet.Check("layout", violations, "Move the file to match docs/internals/source-layout.md.");
    }

    private static IEnumerable<string> OperationFolders(string operations) =>
        Directory.Exists(operations) ? Directory.EnumerateDirectories(operations).SelectMany(Directory.EnumerateDirectories) : [];

    private static IEnumerable<string> CheckOperation(string root, string folder)
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
        if (!files.Contains(name + "Handler.cs"))
        {
            yield return $"handler: {Relative(root, folder)}";
        }
        var dialects = Dialects.Count(d => files.Contains($"{name}.{d}.sql"));
        if (dialects is not 0 && dialects != Dialects.Length)
        {
            yield return $"dialects: {Relative(root, folder)}";
        }
    }

    // Schema SQL has no operation folder, so its dialects are held per stem. The catalog loads by stem, so a stem has
    // one file per dialect across the whole project.
    private static IEnumerable<string> SqlStems(string project)
    {
        var files = Directory
            .EnumerateFiles(project, "*.sql", SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(project, path), Parts: Path.GetFileName(path).Split('.')))
            .Where(file => file.Parts.Length == 3 && Dialects.Contains(file.Parts[1]))
            .ToList();
        foreach (var twice in files.GroupBy(file => $"{file.Parts[0]}.{file.Parts[1]}").Where(group => group.Count() > 1))
        {
            yield return $"duplicate: {twice.Key}";
        }
        foreach (
            var stem in files.Where(file => file.Relative.StartsWith("Schema", StringComparison.Ordinal)).GroupBy(file => file.Parts[0])
        )
        {
            var partial = stem.Select(file => file.Parts[1]).Distinct().Count() != Dialects.Length;
            if (partial != SingleDialectStems.Contains(stem.Key))
            {
                yield return partial ? $"dialects: Schema/{stem.Key}" : $"single-dialect stem now has all three: {stem.Key}";
            }
        }
    }

    // SQL lives beside its operation or in a project's Schema folder; anything else is keyed by its top folder.
    private static IEnumerable<string> SqlPlacement() =>
        ShippedCode
            .SourceFiles("*.sql")
            .Select(file => file.Relative.Split('/'))
            .Where(parts => parts.Length > 3 && parts[2] is not "Operations" and not "Schema")
            .Select(parts => $"sql-placement: {parts[0]}/{parts[1]}/{parts[2]}");

    // Operations are the blocks the engine composes, so the dependency runs one way. The scan is by name, crefs included:
    // it cannot see an Engine type reached only through a member of a non-Engine type, so no such member returns one.
    [Fact]
    public void Operations_never_reference_the_engine()
    {
        var files = ShippedCode.SourceFiles("*.cs").Where(file => file.Relative.StartsWith("src/Acta/", StringComparison.Ordinal)).ToList();
        var engineTypes = files
            .Where(file => file.Relative.StartsWith("src/Acta/Engine/", StringComparison.Ordinal))
            .SelectMany(file => DeclaredTypes(file.Path))
            .ToHashSet();
        Assert.NotEmpty(engineTypes);

        var violations = files
            .Where(file => file.Relative.StartsWith("src/Acta/Operations/", StringComparison.Ordinal))
            .SelectMany(file => NamesUsed(file.Path).Where(engineTypes.Contains).Distinct().Select(name => $"{file.Relative}: {name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(violations.Count == 0, "Operations files that name an Engine type:\n" + string.Join("\n", violations));
    }

    private static IEnumerable<string> DeclaredTypes(string path)
    {
        var nodes = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes().ToList();
        return nodes
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(type => type.Identifier.Text)
            .Concat(nodes.OfType<DelegateDeclarationSyntax>().Select(type => type.Identifier.Text));
    }

    private static IEnumerable<string> NamesUsed(string path) =>
        CSharpSyntaxTree
            .ParseText(File.ReadAllText(path))
            .GetRoot()
            .DescendantNodes(descendIntoTrivia: true)
            .OfType<SimpleNameSyntax>()
            .Select(name => name.Identifier.Text);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
