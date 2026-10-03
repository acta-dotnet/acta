using System.Data.Common;

namespace Acta.Runtime;

/// <summary>
/// A tag target the SQL can find: its scope plus the row id the tags table joins on, or, for tenants,
/// namespaces, and schedules, the name it carries.
/// </summary>
internal sealed record ResolvedTagTarget(TagScopeCode ScopeCode, long? LookupId, string? LookupName)
{
    /// <summary>Binds the target parameters every tag statement takes.</summary>
    public void Bind(DbCommand cmd, ISqlDialect dialect)
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagTargetScopeCode, (byte)ScopeCode));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagTargetLookupId, LookupId));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagTargetLookupName, LookupName));
    }
}
