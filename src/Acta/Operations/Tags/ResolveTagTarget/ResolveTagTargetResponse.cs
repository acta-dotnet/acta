using System.Data.Common;

namespace Acta;

/// <summary>
/// A tag target the SQL can find: its entity kind plus the row id the tags table joins on, or, for tenants and
/// namespaces, the name it carries.
/// </summary>
internal sealed record ResolveTagTargetResponse(EntityCode EntityCode, long? LookupId, string? LookupName)
{
    /// <summary>Binds the target parameters every tag statement takes.</summary>
    public void Bind(DbCommand cmd, ISqlDialect dialect)
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagTargetEntityCode, (byte)EntityCode));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagTargetLookupId, LookupId));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagTargetLookupName, LookupName));
    }
}
