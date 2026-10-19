using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Acta.Testing;

/// <summary>Test-only fluent access to the relational entity model.</summary>
internal static class DbSessionQueryExtensions
{
    public static DbFrom<TEntity> From<TEntity>(this IDbSession session)
        where TEntity : class, IEntity => new(session);
}

/// <summary>
/// Fluent reader/writer accessor over Acta's generated table metadata: composes reads
/// (<c>Where/ToListAsync/etc.</c>) and writes (<c>UpdateOnlyAsync/DeleteAsync/InsertAsync</c>)
/// against the generator-emitted <see cref="DbEntitySpec"/> (<c>ActaSchema.For&lt;TEntity&gt;()</c>)
/// rather than runtime attribute reflection. Writes source their WHERE from the accumulated
/// <see cref="Where"/> filter and require it. Unlike named
/// operations, <c>From&lt;T&gt;</c> writes (<see cref="InsertAsync{TKey}"/>, <see cref="UpdateOnlyAsync"/>,
/// <see cref="DeleteAsync"/>) run on a raw connection with no transaction; they share the session's
/// bounded deadlock retry so fixture writes survive being picked as a victim under parallel tests.
/// </summary>
internal sealed class DbFrom<TEntity>
    where TEntity : class, IEntity
{
    private readonly IDbSession _session;
    private readonly DbEntitySpec _entity;
    private readonly List<LambdaExpression> _where = [];

    internal DbFrom(IDbSession session)
    {
        _session = session;
        _entity = ActaSchema.For<TEntity>();
    }

    public DbFrom<TEntity> Where(Expression<Func<TEntity, bool>> predicate)
    {
        _where.Add(predicate);
        return this;
    }

    public async Task<IReadOnlyList<TEntity>> ToListAsync(CancellationToken ct)
    {
        var (conn, cmd) = await BuildCommand(ct);
        await using (conn)
        await using (cmd)
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            var list = new List<TEntity>();
            while (await reader.ReadAsync(ct))
            {
                list.Add(EntityBinder.Bind<TEntity>(reader));
            }
            return list;
        }
    }

    public async Task<TEntity?> SingleOrDefaultAsync(CancellationToken ct)
    {
        var (conn, cmd) = await BuildCommand(ct);
        await using (conn)
        await using (cmd)
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }
            var first = EntityBinder.Bind<TEntity>(reader);
            return await reader.ReadAsync(ct)
                ? throw new InvalidOperationException($"SingleOrDefaultAsync on {typeof(TEntity).Name} matched more than one row.")
                : first;
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

    /// <summary>
    /// Update only the columns assigned in <paramref name="set"/> (a <c>() =&gt; new T { Col = value }</c>
    /// member-init) for rows matching the accumulated <see cref="Where"/> filter. Returns the number of
    /// rows affected. A write with no filter throws.
    /// </summary>
    public Task<int> UpdateOnlyAsync(Expression<Func<TEntity>> set, CancellationToken ct)
    {
        RequireFilter();
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
    /// affected. A write with no filter throws.
    /// </summary>
    public Task<int> DeleteAsync(CancellationToken ct)
    {
        RequireFilter();
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
        // An explicit transaction, so an attempt that fails anywhere, its commit included, rolls back whole
        // and the retry starts clean: an autocommit INSERT ... RETURNING on SQLite once reported a transient
        // failure yet left its row for the retry to collide with.
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var sb = new StringBuilder();

        DbColumnSpec? identity = null;
        var columns = new List<DbColumnSpec>();
        foreach (var col in _entity.Columns)
        {
            if (col.IsIdentity)
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
            DbValueCoercion.ApplyType(p, clrType, _session.Provider);
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
            await tx.CommitAsync(ct);
            return ((IEntity<TKey>)entity).Id;
        }
        var scalar = await cmd.ExecuteScalarAsync(ct);
        await tx.CommitAsync(ct);
        return (TKey)Convert.ChangeType(scalar!, typeof(TKey), CultureInfo.InvariantCulture);
    }

    private void RequireFilter()
    {
        if (_where.Count == 0)
        {
            throw new InvalidOperationException($"{typeof(TEntity).Name} write has no Where filter.");
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

    private async Task<(DbConnection conn, DbCommand cmd)> BuildCommand(CancellationToken ct)
    {
        var conn = await _session.OpenConnectionAsync(ct);
        DbCommand? cmd = null;
        try
        {
            cmd = conn.CreateCommand();
            var sb = new StringBuilder();

            sb.Append("SELECT ");
            for (var i = 0; i < _entity.Columns.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(QuoteCol(_entity.Columns[i].Name));
            }
            sb.Append(" FROM ").Append(QualifyTable());
            AppendWhere(sb, cmd);

            cmd.CommandText = sb.ToString();
            return (conn, cmd);
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

    private string QualifyTable() => $"{_session.Schema}.{_entity.TableName}";

    private static string QuoteCol(string col) => col;
}

/// <summary>
/// Translates a <c>Where</c> lambda to a SQL fragment plus bound parameters. Supports
/// <c>== != &lt; &lt;= &gt; &gt;=</c>, <c>&amp;&amp; ||</c>, null checks, and
/// <c>Enumerable.Contains</c> as <c>IN</c>; anything else throws.
/// </summary>
internal sealed class WhereVisitor(
    DbEntitySpec entity,
    ParameterExpression entityParam,
    DbProvider provider,
    StringBuilder sb,
    DbCommand command,
    int startCounter
) : ExpressionVisitor
{
    private readonly DbEntitySpec _entity = entity;
    private readonly ParameterExpression _entityParam = entityParam;
    private readonly DbProvider _provider = provider;
    private readonly StringBuilder _sb = sb;
    private readonly DbCommand _command = command;

    public int ParamCounter { get; private set; } = startCounter;

    public void Render(Expression body) => Visit(body);

    protected override Expression VisitBinary(BinaryExpression node)
    {
        switch (node.NodeType)
        {
            case ExpressionType.AndAlso:
                _sb.Append('(');
                Visit(node.Left);
                _sb.Append(" AND ");
                Visit(node.Right);
                _sb.Append(')');
                return node;
            case ExpressionType.OrElse:
                _sb.Append('(');
                Visit(node.Left);
                _sb.Append(" OR ");
                Visit(node.Right);
                _sb.Append(')');
                return node;
            case ExpressionType.Equal:
            case ExpressionType.NotEqual:
            case ExpressionType.LessThan:
            case ExpressionType.LessThanOrEqual:
            case ExpressionType.GreaterThan:
            case ExpressionType.GreaterThanOrEqual:
                EmitCompare(node);
                return node;
        }
        throw new NotSupportedException($"Unsupported binary operator '{node.NodeType}' in predicate.");
    }

    private void EmitCompare(BinaryExpression node)
    {
        if (IsNullConstant(node.Right))
        {
            Visit(node.Left);
            _sb.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL");
            return;
        }
        if (IsNullConstant(node.Left))
        {
            Visit(node.Right);
            _sb.Append(node.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL");
            return;
        }

        _sb.Append('(');
        Visit(node.Left);
        _sb.Append(
            node.NodeType switch
            {
                ExpressionType.Equal => " = ",
                ExpressionType.NotEqual => " <> ",
                ExpressionType.LessThan => " < ",
                ExpressionType.LessThanOrEqual => " <= ",
                ExpressionType.GreaterThan => " > ",
                ExpressionType.GreaterThanOrEqual => " >= ",
                _ => throw new InvalidOperationException(),
            }
        );
        Visit(node.Right);
        _sb.Append(')');
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression == _entityParam)
        {
            var col =
                _entity.FindByClrProperty(node.Member.Name)
                ?? throw new InvalidOperationException(
                    $"Predicate references '{_entity.ClrType.Name}.{node.Member.Name}' which is not a [DbColumn] property."
                );
            _sb.Append(QuoteCol(col.Name));
            return node;
        }

        // Captured local or chained property access: evaluate to a constant and bind.
        EmitParameter(Evaluate(node), node.Type);
        return node;
    }

    protected override Expression VisitConstant(ConstantExpression node)
    {
        EmitParameter(node.Value, node.Type);
        return node;
    }

    protected override Expression VisitUnary(UnaryExpression node)
    {
        if (node.NodeType == ExpressionType.Convert || node.NodeType == ExpressionType.ConvertChecked)
        {
            Visit(node.Operand);
            return node;
        }
        if (node.NodeType == ExpressionType.Not)
        {
            _sb.Append("NOT (");
            Visit(node.Operand);
            _sb.Append(')');
            return node;
        }
        return base.VisitUnary(node);
    }

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // Enumerable.Contains(values, j.X) or list.Contains(j.X): j.X IN (@p1, @p2, ...)
        if (node.Method.Name == "Contains")
        {
            Expression? source = null;
            Expression? item = null;
            if (node.Object is not null && node.Arguments.Count == 1)
            {
                source = node.Object;
                item = node.Arguments[0];
            }
            else if (node.Arguments.Count == 2)
            {
                source = node.Arguments[0];
                item = node.Arguments[1];
            }
            if (source is not null && item is not null && item is MemberExpression me && me.Expression == _entityParam)
            {
                var col =
                    _entity.FindByClrProperty(me.Member.Name)
                    ?? throw new InvalidOperationException($"Predicate references '{me.Member.Name}' which is not a [DbColumn] property.");
                var values = (System.Collections.IEnumerable)Evaluate(source)!;
                var paramNames = new List<string>();
                foreach (var v in values)
                {
                    paramNames.Add(EmitParameterReturningName(v, v?.GetType() ?? typeof(object)));
                }
                _sb.Append(QuoteCol(col.Name)).Append(" IN (").Append(string.Join(", ", paramNames)).Append(')');
                return node;
            }
        }
        throw new NotSupportedException($"Method '{node.Method.DeclaringType?.Name}.{node.Method.Name}' is not supported in predicates.");
    }

    private static bool IsNullConstant(Expression e) => e is ConstantExpression c && c.Value is null;

    private static object? Evaluate(Expression e) => Expression.Lambda(e).Compile().DynamicInvoke();

    private static string QuoteCol(string columnName) => columnName;

    private void EmitParameter(object? value, Type clrType)
    {
        var name = EmitParameterReturningName(value, clrType);
        _sb.Append(name);
    }

    private string EmitParameterReturningName(object? value, Type clrType)
    {
        var name = "@p" + ParamCounter.ToString(CultureInfo.InvariantCulture);
        ParamCounter++;
        var p = _command.CreateParameter();
        p.ParameterName = name;
        p.Value = DbValueCoercion.Coerce(value, clrType, _provider);
        DbValueCoercion.ApplyType(p, clrType, _provider);
        _command.Parameters.Add(p);
        return name;
    }
}

/// <summary>
/// Translates the <c>() =&gt; new T { Col = value, ... }</c> body of an update into a
/// <c>col = @sN</c> SET fragment plus bound parameters. Each assignment's right-hand side is evaluated
/// to a value (captured locals and expressions are folded), then bound through the same coercion the
/// predicate translator uses.
/// </summary>
internal static class DbSetClauseBuilder
{
    public static void Append(Expression setBody, DbEntitySpec entity, DbProvider provider, StringBuilder sb, DbCommand cmd)
    {
        if (setBody is not MemberInitExpression init)
        {
            throw new NotSupportedException(
                "UpdateOnlyAsync set selector must be a member-init expression, e.g. () => new T { Col = value }."
            );
        }
        if (init.Bindings.Count == 0)
        {
            throw new NotSupportedException("UpdateOnlyAsync set selector assigns no columns.");
        }

        for (var i = 0; i < init.Bindings.Count; i++)
        {
            if (init.Bindings[i] is not MemberAssignment assign)
            {
                throw new NotSupportedException(
                    $"Unsupported binding '{init.Bindings[i].BindingType}' in set selector; only direct property assignments are allowed."
                );
            }
            if (i > 0)
            {
                sb.Append(", ");
            }
            var col =
                entity.FindByClrProperty(assign.Member.Name)
                ?? throw new InvalidOperationException(
                    $"Set selector references '{entity.ClrType.Name}.{assign.Member.Name}' which is not a [DbColumn] property."
                );

            // DbFn.UtcNow is a SQL marker: emit the server clock function, not a bound parameter.
            if (IsUtcNowMarker(assign.Expression))
            {
                if (col.Kind != DbKind.UtcInstant)
                {
                    throw new InvalidOperationException(
                        $"DbFn.UtcNow can only target a DbKind.UtcInstant column; '{col.Name}' is {col.Kind}."
                    );
                }
                sb.Append(QuoteCol(col.Name)).Append(" = ").Append(UtcNowSql(provider));
                continue;
            }

            var name = "@s" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(QuoteCol(col.Name)).Append(" = ").Append(name);

            var value = Expression.Lambda(assign.Expression).Compile().DynamicInvoke();
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = DbValueCoercion.Coerce(value, assign.Expression.Type, provider);
            DbValueCoercion.ApplyType(p, assign.Expression.Type, provider);
            cmd.Parameters.Add(p);
        }
    }

    private static string QuoteCol(string col) => col;

    private static bool IsUtcNowMarker(Expression e) =>
        e is MemberExpression m && m.Member.DeclaringType == typeof(DbFn) && m.Member.Name == nameof(DbFn.UtcNow);

    private static string UtcNowSql(DbProvider provider) =>
        provider switch
        {
            DbProvider.SqlServer => "SYSUTCDATETIME()",
            DbProvider.Sqlite => "CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)",
            _ => "now()",
        };
}
