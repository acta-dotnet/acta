using Acta.Tests.Conformance.Testing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// Holds <c>src/</c> to the operation layout in <c>docs/internals/source-layout.md</c>: <c>Operations/Group/Operation/</c>
/// and no deeper, files named after their operation, all three dialects or none, and SQL only under <c>Operations/</c>,
/// <c>Schema/Views/</c>, and a provider's <c>Schema/Migrations/</c>, every view in all three dialects, and no stem twice
/// in one dialect. Each operation has a handler, and no Operations file names a type declared under <c>Engine/</c>
/// outside <c>Engine/Wakeup/</c>, while only Operations files and registration name a handler. A group's root holds its
/// operations class and the interfaces that class implements; a type several operations share sits in the <c>Shared/</c>
/// folder of the nearest folder holding them all, and one that code outside Operations names sits in <c>Domain/</c>.
/// A route folder in <c>Acta.AspNetCore</c> keeps the same depth and file names. What still differs is listed in the
/// <c>layout</c> ratchet.
/// </summary>
public sealed class OperationLayoutTests
{
    private static readonly string[] Dialects = ["pg", "mssql", "sqlite"];

    private static readonly string Views = Path.Combine("Schema", "Views") + Path.DirectorySeparatorChar;

    /// <summary>Operations may name the types under this folder, because a committed write wakes the engine's workers through its publisher.</summary>
    private const string WakeupSeam = "src/Acta/Engine/Wakeup/";

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

    // A group's Shared/ folder holds what several of its operations share; it is not an operation.
    private static IEnumerable<string> OperationFolders(string operations) =>
        Directory.Exists(operations)
            ? Directory
                .EnumerateDirectories(operations)
                .SelectMany(Directory.EnumerateDirectories)
                .Where(folder => Path.GetFileName(folder) != "Shared")
            : [];

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
            .Where(file =>
                file.Relative.StartsWith("src/Acta/Engine/", StringComparison.Ordinal)
                && !file.Relative.StartsWith(WakeupSeam, StringComparison.Ordinal)
            )
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

    /// <summary>
    /// A group's handlers are its inside: code elsewhere calls the group's operations class, and only registration wires a
    /// handler. <c>Domain/</c> names no row either. Crefs count, as above.
    /// </summary>
    [Fact]
    public void Only_operations_name_a_handler()
    {
        var files = ShippedCode.SourceFiles("*.cs").ToList();
        var operations = files.Where(file => file.Relative.StartsWith("src/Acta/Operations/", StringComparison.Ordinal)).ToList();
        var handlers = operations
            .Where(file => file.Relative.EndsWith("Handler.cs", StringComparison.Ordinal))
            .SelectMany(file => DeclaredTypes(file.Path))
            .ToHashSet();
        var rows = operations
            .Where(file => file.Relative.EndsWith("Row.cs", StringComparison.Ordinal))
            .SelectMany(file => DeclaredTypes(file.Path))
            .ToHashSet();
        Assert.NotEmpty(handlers);
        Assert.NotEmpty(rows);
        var handlersOrRows = handlers.Union(rows).ToHashSet();

        var violations = files
            .Except(operations)
            .Where(file => file.Relative != "src/Acta/ActaServiceCollectionExtensions.cs")
            .SelectMany(file =>
            {
                var banned = file.Relative.StartsWith("src/Acta/Domain/", StringComparison.Ordinal) ? handlersOrRows : handlers;
                return NamesUsed(file.Path).Where(banned.Contains).Distinct().Select(name => $"{file.Relative}: {name}");
            })
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Call the group's operations class instead. Files that name a handler, or a row from Domain/:\n" + string.Join("\n", violations)
        );
    }

    /// <summary>
    /// A group's root shows its operations class, <c>&lt;Group&gt;Operations</c>, and the interfaces that class implements,
    /// directly or through each other; the <c>Operations/</c> root shows <c>ActaOperations</c> and its interface, beside the
    /// groups and <c>Shared/</c>. Below <c>Domain/</c> sit <c>Codes/</c>, <c>Entities/</c>, and one folder per group, named for it.
    /// </summary>
    [Fact]
    public void Each_group_root_shows_its_operations_class_and_its_interfaces()
    {
        var acta = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta");
        var operations = Path.Combine(acta, "Operations");
        string[] operationsRoot = ["ActaOperations.cs", "IActaOperations.cs"];
        var violations = new List<string>();

        violations.AddRange(
            Directory
                .EnumerateFiles(operations)
                .Select(Path.GetFileName)
                .Where(file => !operationsRoot.Contains(file))
                .Select(file => $"operations-root: {file}")
        );
        foreach (var group in Directory.EnumerateDirectories(operations).Where(dir => Path.GetFileName(dir) != "Shared"))
        {
            var name = Path.GetFileName(group);
            var expected = $"{name}Operations";
            var type = Types(Path.Combine(group, $"{expected}.cs"))
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.Identifier.Text == expected);
            if (type is null)
            {
                violations.Add($"operations-class: {name} declares no {expected}");
                continue;
            }

            // The interfaces the class implements, followed through the ones declared beside it.
            var allowed = new HashSet<string>(StringComparer.Ordinal) { $"{expected}.cs" };
            var pending = new Queue<string>(BaseNames(type));
            while (pending.TryDequeue(out var baseName))
            {
                if (allowed.Add($"{baseName}.cs") && Types(Path.Combine(group, $"{baseName}.cs")).FirstOrDefault() is { } declared)
                {
                    foreach (var next in BaseNames(declared))
                    {
                        pending.Enqueue(next);
                    }
                }
            }
            violations.AddRange(
                Directory
                    .EnumerateFiles(group)
                    .Select(Path.GetFileName)
                    .Where(file => !allowed.Contains(file!))
                    .Select(file => $"group-root: {name}/{file}")
            );
        }

        var domain = Path.Combine(acta, "Domain");
        violations.AddRange(
            Directory
                .EnumerateDirectories(domain)
                .Select(Path.GetFileName)
                .Where(dir => dir is not ("Codes" or "Entities") && !Directory.Exists(Path.Combine(operations, dir!)))
                .Select(dir => $"domain-folder: {dir}")
        );

        Assert.True(
            violations.Count == 0,
            "Keep a group's root to its operations class and the interfaces it implements, and its other types in Shared/ or its operations:\n"
                + string.Join("\n", violations.Order(StringComparer.Ordinal))
        );
    }

    /// <summary>
    /// A <c>Shared/</c> folder, wherever it sits in <c>src/</c>, is shared below its parent: its types are named only by the parent's own files, its sibling
    /// folders, and what sits deeper, never from above or beside the parent, so it is internal. Registration may wire one.
    /// Crefs count.
    /// </summary>
    [Fact]
    public void A_shared_type_stays_below_its_parent()
    {
        var files = ShippedCode.SourceFiles("*.cs").ToList();
        var scopeOf = files
            .Where(file => file.Relative.Contains("/Shared/", StringComparison.Ordinal))
            .SelectMany(file =>
                DeclaredTypes(file.Path)
                    .Select(type =>
                        (Type: type, Scope: file.Relative[..(file.Relative.LastIndexOf("/Shared/", StringComparison.Ordinal) + 1)])
                    )
            )
            .DistinctBy(shared => shared.Type)
            .ToDictionary(shared => shared.Type, shared => shared.Scope, StringComparer.Ordinal);
        Assert.NotEmpty(scopeOf);
        var published = files
            .Where(file => file.Relative.Contains("/Shared/", StringComparison.Ordinal))
            .SelectMany(file =>
                Types(file.Path)
                    .Where(type => type.Modifiers.Any(modifier => modifier.Text == "public"))
                    .Select(type => $"{file.Relative}: {type.Identifier.Text} is public")
            );

        var violations = files
            .Where(file => file.Relative != "src/Acta/ActaServiceCollectionExtensions.cs")
            .SelectMany(file =>
                NamesUsed(file.Path)
                    .Distinct()
                    .Where(name => scopeOf.TryGetValue(name, out var scope) && !file.Relative.StartsWith(scope, StringComparison.Ordinal))
                    .Select(name => $"{file.Relative}: {name} ({scopeOf[name]}Shared/)")
            )
            .Concat(published)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Move the type up to the Shared/ folder of the nearest folder holding every user, or to Domain/ when code outside Operations/ names it:\n"
                + string.Join("\n", violations)
        );
    }

    private static IEnumerable<BaseTypeDeclarationSyntax> Types(string path) =>
        File.Exists(path)
            ? CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            : [];

    private static IEnumerable<string> BaseNames(BaseTypeDeclarationSyntax type) =>
        type.BaseList?.Types.Select(baseType => baseType.Type.ToString()) ?? [];

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
