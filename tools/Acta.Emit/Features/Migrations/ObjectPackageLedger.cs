using System.Globalization;
using System.Text.Json;
using Acta.Emit.Shared;
using Acta.Relational.Schema;

namespace Acta.Emit.Features.Migrations;

/// <summary>One released package identity and the content it named.</summary>
internal sealed record ObjectPackageRelease(string Provider, int Major, int Revision, string Hash);

/// <summary>
/// The retained record of every released object package. Regenerating a checked-in hash in place would
/// enforce nothing, because the edit and its new hash land together; keeping the released identities and
/// refusing to rewrite one is what forces changed content to take a new revision before it ships. An
/// identity is frozen the moment it is recorded, deliberately: before the first release that costs a
/// revision bump per pre-release edit, and the release guide says so rather than tracking what shipped.
/// </summary>
internal static class ObjectPackageLedger
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static string PathFor(string repoRoot) => Path.Combine(repoRoot, "src", "Acta.Relational", "Schema", "object-packages.json");

    internal static IReadOnlyList<ObjectPackageRelease> Read(string repoRoot)
    {
        var path = PathFor(repoRoot);
        return File.Exists(path) ? JsonSerializer.Deserialize<List<ObjectPackageRelease>>(File.ReadAllText(path)) ?? [] : [];
    }

    /// <summary>
    /// Every way the recorded ledger and the constants can disagree with the live sources, one line per
    /// problem an operator can act on. Checks the identity this build ships against its content, and the
    /// hand-maintained minimum against the ledger, because the minimum is what startup gates on and
    /// nothing else would stop it naming a revision that was never recorded.
    /// </summary>
    internal static IReadOnlyList<string> Verify(string repoRoot, IReadOnlyDictionary<string, string> hashes)
    {
        var released = Read(repoRoot);
        var problems = new List<string>();

        foreach (var provider in ProviderCatalog.All)
        {
            var recorded = released.FirstOrDefault(r => IsCurrent(r, provider));
            if (recorded is null)
            {
                problems.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"object package {provider.Token} {ObjectPackageStamp.ContractMajor}.{ObjectPackageStamp.PackageRevision} is not recorded; run `Acta.Emit objects record`"
                    )
                );
            }
            else if (!string.Equals(recorded.Hash, hashes[provider.Token], StringComparison.Ordinal))
            {
                problems.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"object package {provider.Token} {recorded.Major}.{recorded.Revision} was released as {recorded.Hash} but its objects now hash to {hashes[provider.Token]}; a released identity is immutable, so raise ObjectPackageStamp.PackageRevision and run `Acta.Emit objects record`"
                    )
                );
            }

            if (!released.Any(r => Names(r, provider, ObjectPackageStamp.MinimumPackageRevision)))
            {
                problems.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"ObjectPackageStamp.MinimumPackageRevision names {provider.Token} {ObjectPackageStamp.ContractMajor}.{ObjectPackageStamp.MinimumPackageRevision}, which the ledger never recorded; the accepted floor must be a released identity"
                    )
                );
            }
        }

        return problems;
    }

    /// <summary>
    /// Records this build's identity for every provider. Refuses to rewrite a recorded identity that named
    /// different content, which is the whole point of retaining it.
    /// </summary>
    internal static void Record(string repoRoot, IReadOnlyDictionary<string, string> hashes)
    {
        var released = Read(repoRoot).ToList();

        foreach (var provider in ProviderCatalog.All)
        {
            var live = hashes[provider.Token];
            var existing = released.FirstOrDefault(r => IsCurrent(r, provider));
            if (existing is null)
            {
                released.Add(
                    new ObjectPackageRelease(provider.Token, ObjectPackageStamp.ContractMajor, ObjectPackageStamp.PackageRevision, live)
                );
            }
            else if (!string.Equals(existing.Hash, live, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Object package {provider.Token} {ObjectPackageStamp.ContractMajor}.{ObjectPackageStamp.PackageRevision} was already released as {existing.Hash}. A released identity is immutable: raise ObjectPackageStamp.PackageRevision and record that instead."
                    )
                );
            }
        }

        var path = PathFor(repoRoot);
        File.WriteAllText(path, JsonSerializer.Serialize(released, Json).ReplaceLineEndings("\n") + "\n");
        Console.WriteLine($"  wrote {path}");
    }

    private static bool IsCurrent(ObjectPackageRelease release, ProviderInfo provider) =>
        Names(release, provider, ObjectPackageStamp.PackageRevision);

    private static bool Names(ObjectPackageRelease release, ProviderInfo provider, int revision) =>
        string.Equals(release.Provider, provider.Token, StringComparison.Ordinal)
        && release.Major == ObjectPackageStamp.ContractMajor
        && release.Revision == revision;
}
