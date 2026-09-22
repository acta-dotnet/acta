using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta.Relational.Schema;

/// <summary>
/// What this build requires of the object package a database has installed. The major must match
/// exactly, because a higher one is the incompatibility the number exists to signal rather than a
/// newer thing to accept; the revision only has to clear the minimum, which is what lets a worker keep
/// running against a package a newer deploy installed and what keeps a rollback startable.
/// </summary>
internal readonly record struct ObjectPackageRequirement(int ContractMajor, int MinimumPackageRevision);

/// <summary>
/// The versionless half of the schema carries no version of its own: operator views and stored routines
/// are rewritten by whatever bootstrap last applied migrations, so a host with migrations disabled can
/// pass the history preflight while calling bodies from an older build. This names the installed set, as
/// one sentinel row in <c>migrations</c>.
/// </summary>
internal static class ObjectPackageStamp
{
    /// <summary>
    /// The family of mutually compatible object contracts. Moves only when compatibility breaks, and
    /// startup requires equality in both directions.
    /// </summary>
    internal const int ContractMajor = 1;

    /// <summary>
    /// An ordered revision within <see cref="ContractMajor"/>, moved whenever the installed content
    /// changes. <c>Acta.Emit check</c> refuses a released revision carrying different content.
    /// </summary>
    internal const int PackageRevision = 1;

    /// <summary>
    /// The oldest revision this build can call, which is deliberately not the revision it ships. A
    /// release carrying only an optimization still accepts the revision before it, so a rollback onto
    /// that package keeps starting; raising this narrows the supported deploy window and is a decision,
    /// not a side effect of shipping.
    /// </summary>
    internal const int MinimumPackageRevision = 1;

    /// <summary>
    /// The <c>migrations.version</c> the stamp row occupies. Negative so it sits outside the migration
    /// sequence, beside the version-0 baseline sentinel, and trips none of the history verdicts.
    /// </summary>
    internal const int HistoryVersion = -1;

    internal static ObjectPackageRequirement Required => new(ContractMajor, MinimumPackageRevision);

    /// <summary>The stamp literal for a hash, as written into <c>migrations.name</c>.</summary>
    internal static string Format(string contentHash) =>
        string.Create(CultureInfo.InvariantCulture, $"objects-{ContractMajor}.{PackageRevision}-{contentHash}");

    /// <summary>
    /// Reads the two numbers startup decides on. The hash is deliberately not returned: comparing it for
    /// equality would refuse an older worker against newer compatible objects, which rolling deploys are
    /// promised to support, so it exists for build-time bookkeeping and never for the startup verdict.
    /// </summary>
    internal static bool TryParse(string? recorded, out int contractMajor, out int packageRevision)
    {
        contractMajor = 0;
        packageRevision = 0;
        if (recorded is null || !recorded.StartsWith("objects-", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = recorded["objects-".Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0)
        {
            return false;
        }

        var version = rest[..dash];
        var dot = version.IndexOf('.', StringComparison.Ordinal);
        return dot > 0
            && int.TryParse(version[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out contractMajor)
            && int.TryParse(version[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out packageRevision);
    }

    /// <summary>
    /// Identity of one provider's installed set: every object's install name and body, in the order the
    /// installer applies them, normalized to LF so a checkout's line endings are not part of the identity.
    /// </summary>
    internal static string HashOf(IEnumerable<(string Name, string Body)> objects)
    {
        var text = new StringBuilder();
        foreach (var (name, body) in objects)
        {
            text.Append(name).Append('\n').Append(body.ReplaceLineEndings("\n")).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
