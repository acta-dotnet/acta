using System.Data.Common;

namespace Acta;

/// <summary>
/// The transaction to read in: the migration transaction, which may have just created the table, or none for the read-only
/// preflight, which takes no lock and writes nothing, so there is no boundary for it to join.
/// </summary>
internal sealed record LoadAppliedVersionsQuery(DbTransaction? Transaction);
