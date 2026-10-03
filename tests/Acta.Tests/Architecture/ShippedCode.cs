using System.Reflection;
using Acta.Tests.Conformance.Testing;

namespace Acta.Tests.Architecture;

/// <summary>The code Acta ships: the files under <c>src/</c>, and the assemblies built from them and their types.</summary>
internal static class ShippedCode
{
    // Every src project but the analyzer, which runs in the compiler rather than in an application.
    public static IReadOnlyList<Assembly> Assemblies { get; } =
    [
        .. Directory
            .EnumerateFiles(Path.Combine(IntegrationConfig.FindRepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name != "Acta.Generators")
            .Select(name => Assembly.Load(name!)),
    ];

    // Compiler output has unspeakable names, and polyfills live outside the Acta namespaces.
    public static IReadOnlyList<Type> Types { get; } =
    [
        .. Assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type =>
                !type.Name.Contains('<', StringComparison.Ordinal) && type.Namespace?.StartsWith("Acta", StringComparison.Ordinal) == true
            ),
    ];

    /// <summary><c>Outer.Name</c> without namespace, assembly, or generic arity, so it survives a move.</summary>
    public static string Key(Type type) => type.DeclaringType is null ? SimpleName(type) : Key(type.DeclaringType) + "." + SimpleName(type);

    public static string SimpleName(Type type) => type.Name.Split('`')[0];

    /// <summary>Files under <c>src/</c> matching <paramref name="pattern"/>, with their repo-relative path, outside build output.</summary>
    public static IEnumerable<(string Path, string Relative)> SourceFiles(string pattern)
    {
        var root = IntegrationConfig.FindRepoRoot();
        return Directory
            .EnumerateFiles(System.IO.Path.Combine(root, "src"), pattern, SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: System.IO.Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(file =>
                !file.Relative.Contains("/obj/", StringComparison.Ordinal) && !file.Relative.Contains("/bin/", StringComparison.Ordinal)
            );
    }
}
