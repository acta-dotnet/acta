using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Acta.Relational.Commands;
using Acta.Relational.Schema;

namespace Acta.Testing.Relational.Querying;

/// <summary>Looks up a column spec by CLR property name.</summary>
internal static class EntitySpecLookups
{
    public static DbColumnSpec? FindByClrProperty(this DbEntitySpec spec, string clrPropertyName)
    {
        foreach (var col in spec.Columns)
        {
            if (col.ClrPropertyName == clrPropertyName)
            {
                return col;
            }
        }
        return null;
    }
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
        _command.Parameters.Add(p);
        return name;
    }
}
