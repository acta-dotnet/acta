using System.Data.Common;

namespace Acta.Runtime;

/// <summary>
/// The <c>ApplyTags</c> routine the three tag writes share until Phase 10 gives each its own inline SQL.
/// The kind selects the write.
/// </summary>
internal static class TagWrites
{
    public static void Bind(DbCommand cmd, ISqlDialect dialect, ResolvedTagTarget target, TagMutationKind kind, string itemsJson)
    {
        target.Bind(cmd, dialect);
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagMutation, (byte)kind));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagItemsJson, itemsJson));
    }
}

/// <summary>The write <c>ApplyTags</c> performs.</summary>
internal enum TagMutationKind : byte
{
    Replace = 1,
    Upsert = 2,
    Remove = 3,
}
