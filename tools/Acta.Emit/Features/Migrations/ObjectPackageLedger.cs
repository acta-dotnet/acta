using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Acta.Emit.Shared;
using Acta.Relational.Schema;

namespace Acta.Emit.Features.Migrations;

/// <summary>One released package identity and the content it named.</summary>
internal sealed record ObjectPackageRelease(string Provider, int Major, int Revision, string Hash);

/// <summary>
/// The retained record of every released object package. Regenerating a checked-in hash in place would
/// enforce nothing, because the edit and its new hash land together; keeping the released identities and
/// refusing to rewrite one is what forces changed content to take a new revision before it ships.
/// </summary>
internal static class ObjectPackageLedger
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static string PathFor(string repoRoot) => Path.Combine(repoRoot, "src", "Acta.Relational", "Schema", "object-packages.json");

    internal static IReadOnlyList<ObjectPackageRelease> Read(string repoRoot)
    {
        var path = PathFor(repoRoot);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<ObjectPackageRelease>>(File.ReadAllText(path)) ?? [] : [];
    }

    /// <summary>
    /// Whether every provider's live content matches what the ledger recorded for the identity this build
    /// declares. Returns the verdicts an operator can act on, one line per provider that disagrees.
    /// </summary>
    internal static IReadOnlyList<string> Verify(string repoRoot)
    {
        var released = Read(repoRoot);
        var problems = new List<string>();

        foreach (var provider in ProviderCatalog.All)
        {
            var live = ObjectPackageEmitter.HashFor(repoRoot, provider.Suffix);
            var recorded = released.FirstOrDefault(r =>
                string.Equals(r.Provider, provider.Token, StringComparison.Ordinal)
                && r.Major == ObjectPackageStamp.ContractMajor
                && r.Revision == ObjectPackageStamp.PackageRevision
            );

            if (recorded is null)
            {
                problems.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"object package {provider.Token} {ObjectPackageStamp.ContractMajor}.{ObjectPackageStamp.PackageRevision} is not recorded; run `Acta.Emit objects record`"
                    )
                );
                continue;
            }

            if (!string.Equals(recorded.Hash, live, StringComparison.Ordinal))
            {
                problems.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"object package {provider.Token} {recorded.Major}.{recorded.Revision} was released as {recorded.Hash} but its objects now hash to {live}; a released identity is immutable, so raise ObjectPackageStamp.PackageRevision and run `Acta.Emit objects record`"
                    )
                );
            }
        }

        return problems;
    }

    /// <summary>
    /// Records this build's identity for every provider. Refuses to rewrite a released identity that
    /// named different content, which is the whole point of retaining it.
    /// </summary>
    internal static void Record(string repoRoot)
    {
        var released = Read(repoRoot).ToList();

        foreach (var provider in ProviderCatalog.All)
        {
            var live = ObjectPackageEmitter.HashFor(repoRoot, provider.Suffix);
            var existing = released.FindIndex(r =>
                string.Equals(r.Provider, provider.Token, StringComparison.Ordinal)
                && r.Major == ObjectPackageStamp.ContractMajor
                && r.Revision == ObjectPackageStamp.PackageRevision
            );

            if (existing >= 0)
            {
                if (!string.Equals(released[existing].Hash, live, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Object package {provider.Token} {ObjectPackageStamp.ContractMajor}.{ObjectPackageStamp.PackageRevision} was already released as {released[existing].Hash}. A released identity is immutable: raise ObjectPackageStamp.PackageRevision and record that instead."
                        )
                    );
                }

                continue;
            }

            released.Add(new ObjectPackageRelease(provider.Token, ObjectPackageStamp.ContractMajor, ObjectPackageStamp.PackageRevision, live));
        }

        var path = PathFor(repoRoot);
        File.WriteAllText(path, JsonSerializer.Serialize(released, Json).ReplaceLineEndings("\n") + "\n");
        Console.WriteLine($"  wrote {path}");
    }
}
