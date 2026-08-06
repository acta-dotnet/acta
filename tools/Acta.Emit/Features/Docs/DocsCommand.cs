using Acta.Emit.Shared;
using Acta.Emit.Shared.Model;

namespace Acta.Emit.Features.Docs;

internal static class DocsCommand
{
    internal static int Run() => Emit(RepoRoot.Find());

    /// <summary>Regenerates reference docs under <paramref name="repoRoot"/>. Reused by `schema add/amend`
    /// so a model change keeps docs current (and `check` green) against the same root.</summary>
    internal static int Emit(string repoRoot)
    {
        var model = SchemaModel.Discover();
        var referenceDir = Path.Combine(repoRoot, "docs", "reference");
        Directory.CreateDirectory(referenceDir);

        var dataModelPath = Path.Combine(referenceDir, "data-model.md");
        File.WriteAllText(dataModelPath, DataModelEmitter.EmitDataModelReference(model));
        Console.WriteLine($"  wrote {dataModelPath}");

        var codesPath = Path.Combine(referenceDir, "code-families.md");
        File.WriteAllText(codesPath, CodeFamilyEmitter.EmitCodes(model, repoRoot));
        Console.WriteLine($"  wrote {codesPath}");

        var provisionDir = Path.Combine(referenceDir, "provision");
        Directory.CreateDirectory(provisionDir);
        foreach (var (token, _, _) in ProvisionScriptEmitter.Providers)
        {
            var provisionPath = Path.Combine(provisionDir, token + ".sql");
            File.WriteAllText(provisionPath, ProvisionScriptEmitter.Emit(repoRoot, token));
            Console.WriteLine($"  wrote {provisionPath}");
        }

        return 0;
    }
}
