using System.Data.Common;

namespace Acta;

/// <summary>The parameters every tag write binds: the target, then the normalized items as JSON.</summary>
internal static class TagWrites
{
    public static void Bind(DbCommand cmd, ISqlDialect dialect, ResolvedTagTarget target, string itemsJson)
    {
        target.Bind(cmd, dialect);
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagItemsJson, itemsJson));
    }
}
