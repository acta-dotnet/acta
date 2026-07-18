using Acta.Relational.Connections;
using Acta.Relational.Schema;

namespace Acta.Testing.Relational.Querying;

/// <summary>Test-only fluent access to the relational entity model.</summary>
internal static class DbSessionQueryExtensions
{
    public static DbFrom<TEntity, TEntity> From<TEntity>(this IDbSession session)
        where TEntity : class, IEntity => new(session);

    public static DbFrom<TEntity, TProjection> From<TEntity, TProjection>(this IDbSession session)
        where TEntity : class, IEntity
        where TProjection : class => new(session);
}
