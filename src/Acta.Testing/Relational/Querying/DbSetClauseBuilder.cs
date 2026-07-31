using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Acta.Relational.Commands;
using Acta.Relational.Schema;

namespace Acta.Testing.Relational.Querying;

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
