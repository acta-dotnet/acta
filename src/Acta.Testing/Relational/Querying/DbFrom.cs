using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Acta.Relational.Commands;
using Acta.Relational.Schema;

namespace Acta.Testing.Relational.Querying;

/// <summary>
/// Fluent reader/writer accessor over Acta's generated table metadata: composes reads
/// (<c>Where/OrderBy/Take/ToListAsync/etc.</c>) and writes (<c>UpdateOnlyAsync/DeleteAsync/InsertAsync</c>)
/// against the generator-emitted <see cref="DbEntitySpec"/> (<c>ActaSchema.For&lt;TEntity&gt;()</c>)
/// rather than runtime attribute reflection. Writes source their WHERE from the accumulated
/// <see cref="Where"/> filter and require it (or an explicit <see cref="All"/>). Unlike named
/// operations, <c>From&lt;T&gt;</c> writes (<see cref="InsertAsync{TKey}"/>, <see cref="UpdateOnlyAsync"/>,
/// <see cref="DeleteAsync"/>) run on a raw connection with no transaction; they share the session's
/// bounded deadlock retry so fixture writes survive being picked as a victim under parallel tests.
/// </summary>
internal sealed class DbFrom<TEntity, TProjection>
    where TEntity : class, IEntity
    where TProjection : class
{
    private readonly IDbSession _session;
    private readonly DbEntitySpec _entity;
    private readonly List<LambdaExpression> _where = [];
    private readonly List<(LambdaExpression Selector, bool Descending)> _orderBy = [];
    private int? _take;
    private bool _all;

    internal DbFrom(IDbSession session)
    {
        _session = session;
        _entity = ActaSchema.For<TEntity>();
    }

    public DbFrom<TEntity, TProjection> Where(Expression<Func<TEntity, bool>> predicate)
    {
        _where.Add(predicate);
        return this;
    }

    public DbFrom<TEntity, TProjection> OrderBy<TKey>(Expression<Func<TEntity, TKey>> selector)
    {
        _orderBy.Add((selector, Descending: false));
        return this;
    }

    public DbFrom<TEntity, TProjection> OrderByDescending<TKey>(Expression<Func<TEntity, TKey>> selector)
    {
        _orderBy.Add((selector, Descending: true));
        return this;
    }

    public DbFrom<TEntity, TProjection> Take(int count)
    {
        _take = count;
        return this;
    }

    /// <summary>Explicit intent for a full-table write: allows Update/Delete with no <see cref="Where"/> filter.</summary>
    public DbFrom<TEntity, TProjection> All()
    {
        _all = true;
        return this;
    }

    public async Task<IReadOnlyList<TProjection>> ToListAsync(CancellationToken ct)
    {
        var (conn, cmd, projectionColumns) = await BuildCommand(ct);
        await using (conn)
        await using (cmd)
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            var list = new List<TProjection>();
            while (await reader.ReadAsync(ct))
            {
                list.Add(DbFrom<TEntity, TProjection>.MaterializeRow(reader, projectionColumns));
            }
            return list;
        }
    }

    public async Task<TProjection?> SingleOrDefaultAsync(CancellationToken ct)
    {
        var (conn, cmd, projectionColumns) = await BuildCommand(ct);
        await using (conn)
        await using (cmd)
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            var first = DbFrom<TEntity, TProjection>.MaterializeRow(reader, projectionColumns);
            return await reader.ReadAsync(ct)
                ? throw new InvalidOperationException($"SingleOrDefaultAsync on {typeof(TEntity).Name} matched more than one row.")
                : first;
        }
    }

    public async Task<TProjection?> FirstOrDefaultAsync(CancellationToken ct)
    {
        _take = 1;
        var (conn, cmd, projectionColumns) = await BuildCommand(ct);
        await using (conn)
        await using (cmd)
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            return await reader.ReadAsync(ct) ? DbFrom<TEntity, TProjection>.MaterializeRow(reader, projectionColumns) : null;
        }
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var conn = await _session.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sb = new StringBuilder();
        sb.Append("SELECT COUNT(*) FROM ").Append(QualifyTable());
        AppendWhere(sb, cmd);
        cmd.CommandText = sb.ToString();
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<bool> AnyAsync(CancellationToken ct) => await CountAsync(ct) > 0;

    /// <summary>
    /// Update only the columns assigned in <paramref name="set"/> (a <c>() =&gt; new T { Col = value }</c>
    /// member-init) for rows matching the accumulated <see cref="Where"/> filter. Returns the number of
    /// rows affected. A write with no filter throws unless <see cref="All"/> was called.
    /// </summary>
    public Task<int> UpdateOnlyAsync(Expression<Func<TEntity>> set, CancellationToken ct)
    {
        RequireFilterOrAll();
        return _session.RunWithRetryAsync(
            async token =>
            {
                await using var conn = await _session.OpenConnectionAsync(token);
                await using var cmd = conn.CreateCommand();
                var sb = new StringBuilder();

                sb.Append("UPDATE ").Append(QualifyTable()).Append(" SET ");
                DbSetClauseBuilder.Append(set.Body, _entity, _session.Provider, sb, cmd);
                AppendWhere(sb, cmd);

                cmd.CommandText = sb.ToString();
                return await cmd.ExecuteNonQueryAsync(token);
            },
            ct
        );
    }

    /// <summary>
    /// Delete rows matching the accumulated <see cref="Where"/> filter. Returns the number of rows
    /// affected. A write with no filter throws unless <see cref="All"/> was called.
    /// </summary>
    public Task<int> DeleteAsync(CancellationToken ct)
    {
        RequireFilterOrAll();
        return _session.RunWithRetryAsync(
            async token =>
            {
                await using var conn = await _session.OpenConnectionAsync(token);
                await using var cmd = conn.CreateCommand();
                var sb = new StringBuilder();

                sb.Append("DELETE FROM ").Append(QualifyTable());
                AppendWhere(sb, cmd);

                cmd.CommandText = sb.ToString();
                return await cmd.ExecuteNonQueryAsync(token);
            },
            ct
        );
    }

    /// <summary>
    /// Insert <paramref name="entity"/>, omitting the DB-assigned identity column and any server-default
    /// columns, and return the key. When the entity has a DB-assigned identity the value is read back
    /// (<c>RETURNING</c> / <c>OUTPUT inserted</c>); otherwise the entity's own <c>Id</c> is returned.
    /// </summary>
    public Task<TKey> InsertAsync<TKey>(TEntity entity, CancellationToken ct)
    {
        return entity is not IEntity<TKey>
            ? throw new InvalidOperationException(
                $"InsertAsync<{typeof(TKey).Name}> does not match {typeof(TEntity).Name}'s key type; "
                    + "TKey must be the entity's IEntity<TId> argument."
            )
            : _session.RunWithRetryAsync(token => InsertCoreAsync<TKey>(entity, token), ct);
    }

    private async Task<TKey> InsertCoreAsync<TKey>(TEntity entity, CancellationToken ct)
    {
        await using var conn = await _session.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sb = new StringBuilder();

        DbColumnSpec? identity = null;
        var columns = new List<DbColumnSpec>();
        foreach (var col in _entity.Columns)
        {
            if (col.IsSolePrimaryKey && !col.IsManualPrimaryKey)
            {
                identity = col;
                continue;
            }
            if (col.HasServerDefault || col.IsGenerated)
            {
                continue;
            }
            columns.Add(col);
        }

        sb.Append("INSERT INTO ").Append(QualifyTable()).Append(" (");
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            sb.Append(QuoteCol(columns[i].Name));
        }
        sb.Append(')');

        if (identity is not null && _session.Provider == DbProvider.SqlServer)
        {
            sb.Append(" OUTPUT inserted.").Append(QuoteCol(identity.Name));
        }

        sb.Append(" VALUES (");
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            var name = "@i" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(name);
            var (value, clrType) = ReadProperty(entity, columns[i].ClrPropertyName);
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = DbValueCoercion.Coerce(value, clrType, _session.Provider);
            cmd.Parameters.Add(p);
        }
        sb.Append(')');

        if (identity is not null && _session.Provider is DbProvider.Postgres or DbProvider.Sqlite)
        {
            sb.Append(" RETURNING ").Append(QuoteCol(identity.Name));
        }

        cmd.CommandText = sb.ToString();

        if (identity is null)
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return ((IEntity<TKey>)entity).Id;
        }
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return (TKey)Convert.ChangeType(scalar!, typeof(TKey), CultureInfo.InvariantCulture);
    }

    private void RequireFilterOrAll()
    {
        if (_where.Count == 0 && !_all)
        {
            throw new InvalidOperationException(
                $"{typeof(TEntity).Name} write has no Where filter. Call All() to write every row on purpose."
            );
        }
    }

    private static (object? Value, Type Type) ReadProperty(object entity, string propertyName)
    {
        var pi =
            entity.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Entity '{entity.GetType().Name}' has no property '{propertyName}' for column mapping."
            );
        return (pi.GetValue(entity), pi.PropertyType);
    }

    private static TProjection MaterializeRow(DbDataReader reader, IReadOnlyList<DbColumnSpec> projectionColumns)
    {
        return typeof(TProjection) == typeof(TEntity)
            ? (TProjection)(object)Materializer.MaterializeEntity<TEntity>(reader)
            : Materializer.MaterializeProjection<TProjection>(reader, projectionColumns);
    }

    private async Task<(DbConnection conn, DbCommand cmd, IReadOnlyList<DbColumnSpec> projectionColumns)> BuildCommand(CancellationToken ct)
    {
        var conn = await _session.OpenConnectionAsync(ct);
        DbCommand? cmd = null;
        try
        {
            cmd = conn.CreateCommand();
            var sb = new StringBuilder();

            IReadOnlyList<DbColumnSpec> projectionColumns =
                typeof(TProjection) == typeof(TEntity) ? _entity.Columns : ResolveProjectionColumns();
            sb.Append("SELECT ");
            for (var i = 0; i < projectionColumns.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(QuoteCol(projectionColumns[i].Name));
            }
            sb.Append(" FROM ").Append(QualifyTable());
            AppendWhere(sb, cmd);
            AppendOrderBy(sb);
            AppendTake(sb);

            cmd.CommandText = sb.ToString();
            return (conn, cmd, projectionColumns);
        }
        catch
        {
            if (cmd is not null)
            {
                await cmd.DisposeAsync();
            }
            await conn.DisposeAsync();
            throw;
        }
    }

    private IReadOnlyList<DbColumnSpec> ResolveProjectionColumns()
    {
        var t = typeof(TProjection);
        var ctor = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(c => c.GetParameters().Length > 0)
            .Where(c => !(c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == t))
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        return ctor is not null
            ? ctor.GetParameters()
                .Select(p =>
                    _entity.FindByClrProperty(p.Name!)
                    ?? throw new InvalidOperationException(
                        $"Projection '{t.Name}' has parameter '{p.Name}' which doesn't match any [DbColumn] CLR property name on '{typeof(TEntity).Name}'."
                    )
                )
                .ToArray()
            :
            [
                .. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite)
                    .Select(p =>
                        _entity.FindByClrProperty(p.Name)
                        ?? throw new InvalidOperationException(
                            $"Projection '{t.Name}' has property '{p.Name}' which doesn't match any [DbColumn] CLR property name on '{typeof(TEntity).Name}'."
                        )
                    ),
            ];
    }

    private void AppendWhere(StringBuilder sb, DbCommand cmd)
    {
        if (_where.Count == 0)
        {
            return;
        }
        sb.Append(" WHERE ");
        var counter = 0;
        for (var i = 0; i < _where.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" AND ");
            }
            var lambda = _where[i];
            var visitor = new WhereVisitor(_entity, (ParameterExpression)lambda.Parameters[0], _session.Provider, sb, cmd, counter);
            visitor.Render(lambda.Body);
            counter = visitor.ParamCounter;
        }
    }

    private void AppendOrderBy(StringBuilder sb)
    {
        if (_orderBy.Count == 0)
        {
            return;
        }
        sb.Append(" ORDER BY ");
        for (var i = 0; i < _orderBy.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }
            var (selector, descending) = _orderBy[i];
            if (selector.Body is not MemberExpression me || me.Expression != selector.Parameters[0])
            {
                throw new InvalidOperationException("OrderBy selector must be a direct entity property access.");
            }
            var col =
                _entity.FindByClrProperty(me.Member.Name)
                ?? throw new InvalidOperationException($"OrderBy references '{me.Member.Name}' which is not a [DbColumn] property.");
            sb.Append(QuoteCol(col.Name));
            if (descending)
            {
                sb.Append(" DESC");
            }
        }
    }

    private void AppendTake(StringBuilder sb)
    {
        if (_take is not { } take)
        {
            return;
        }
        sb.Append(
            _session.Provider switch
            {
                DbProvider.Postgres => " LIMIT " + take.ToString(CultureInfo.InvariantCulture),
                DbProvider.Sqlite => " LIMIT " + take.ToString(CultureInfo.InvariantCulture),
                DbProvider.SqlServer when _orderBy.Count > 0 => " OFFSET 0 ROWS FETCH NEXT "
                    + take.ToString(CultureInfo.InvariantCulture)
                    + " ROWS ONLY",
                DbProvider.SqlServer => RewriteSelectWithTop(sb, take),
                _ => throw new InvalidOperationException($"Unsupported provider '{_session.Provider}'."),
            }
        );
    }

    private static string RewriteSelectWithTop(StringBuilder sb, int take)
    {
        // SQL Server: FETCH requires ORDER BY, so unordered Take splices TOP after SELECT.
        sb.Replace("SELECT ", "SELECT TOP " + take.ToString(CultureInfo.InvariantCulture) + " ", 0, "SELECT ".Length);
        return "";
    }

    private string QualifyTable() => $"{_session.Schema}.{_entity.TableName}";

    private static string QuoteCol(string col) => col;
}
