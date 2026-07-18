using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Discriminator for <c>Lease</c> rows: the <c>leases</c> table carries the named-lock primitives,
/// separated by kind. <see cref="Lock"/> rows back <c>JobContext.RunWithLock</c> (handler / global /
/// namespace lock spaces). Execution ownership/TTL is not a lease row: it lives on the job's
/// <c>runtimes</c> row, where the claim writes it in the same UPDATE as the status transition.
/// </summary>
[JsonConverter(typeof(LeaseKindCodeJsonConverter))]
[CodeKind("lease-kind")]
public enum LeaseKindCode : byte
{
    /// <summary>Mutual-exclusion lock row (handler / global / namespace lock spaces).</summary>
    [Code("lock", "Mutual-exclusion lock row; steal-on-expiry acquire, version-CAS release.")]
    Lock = 10,
}
