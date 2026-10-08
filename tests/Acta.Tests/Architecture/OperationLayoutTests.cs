using Acta.Tests.Conformance.Testing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds <c>src/</c> to the operation layout in <c>docs/internals/source-layout.md</c>: <c>Operations/Group/Operation/</c>
/// and no deeper, files named after their operation, all three dialects or none, and SQL only under <c>Operations/</c>,
/// <c>Schema/Views/</c>, and a provider's <c>Schema/Migrations/</c>, every view in all three dialects, and no stem twice
/// in one dialect. Each operation has a handler, and no Operations file names a type declared under <c>Engine/</c>. A
/// route folder in <c>Acta.AspNetCore</c> keeps the same depth and file names. What still differs is listed in the
/// <c>layout</c> ratchet.
/// </summary>
public sealed class OperationLayoutTests
{
    private static readonly string[] Dialects = ["pg", "mssql", "sqlite"];

    private static readonly string Views = Path.Combine("Schema", "Views") + Path.DirectorySeparatorChar;

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
        foreach (var route in OperationFolders(Path.Combine(root, "src", "Acta.AspNetCore", "Operations")))
        {
            violations.AddRange(CheckFolder(root, route));
        }

        Ratchet.Check("layout", violations, "Move the file to match docs/internals/source-layout.md.");
    }

    private static IEnumerable<string> OperationFolders(string operations) =>
        Directory.Exists(operations) ? Directory.EnumerateDirectories(operations).SelectMany(Directory.EnumerateDirectories) : [];

    // A route folder holds the route's request, response, and endpoint; it has no handler and no SQL of its own.
    private static IEnumerable<string> CheckFolder(string root, string folder)
    {
        var name = Path.GetFileName(folder);
        foreach (var nested in Directory.EnumerateDirectories(folder))
        {
            yield return $"depth: {Relative(root, nested)}";
        }
        foreach (
            var file in Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(f => !f!.StartsWith(name, StringComparison.Ordinal))
        )
        {
            yield return $"file-name: {Relative(root, folder)}/{file}";
        }
    }

    private static IEnumerable<string> CheckOperation(string root, string folder)
    {
        var name = Path.GetFileName(folder);
        foreach (var violation in CheckFolder(root, folder))
        {
            yield return violation;
        }

        var files = Directory.EnumerateFiles(folder).Select(Path.GetFileName).ToList();
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

    // A view has no operation folder, so its dialects are held per stem. The catalog loads by stem, so a stem has one
    // file per dialect across the whole project.
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
        foreach (var view in files.Where(file => file.Relative.StartsWith(Views, StringComparison.Ordinal)).GroupBy(file => file.Parts[0]))
        {
            if (view.Select(file => file.Parts[1]).Distinct().Count() != Dialects.Length)
            {
                yield return $"dialects: Schema/Views/{view.Key}";
            }
        }
    }

    // SQL lives beside its operation, with the views, or with a provider's migrations; anything else is keyed by its folder.
    private static IEnumerable<string> SqlPlacement() =>
        ShippedCode
            .SourceFiles("*.sql")
            .Select(file => file.Relative.Split('/'))
            .Where(parts => parts.Length > 3 && parts[2] is not "Operations" && parts[2..^1] is not ["Schema", "Views" or "Migrations"])
            .Select(parts => $"sql-placement: {string.Join('/', parts[..^1])}");

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
