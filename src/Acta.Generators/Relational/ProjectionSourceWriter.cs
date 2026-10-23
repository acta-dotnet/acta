using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using static Acta.Generators.DbProjectionGenerator;

namespace Acta.Generators;

/// <summary>Writes the projection binders and the resolver that hands each projected row its binder.</summary>
internal static class ProjectionSourceWriter
{
    internal static string EmitNamespaceBinder(string namespaceName, ImmutableArray<ProjectionInfo> projections)
    {
        var sb = NewSourceBuilder();
        AppendNamespace(sb, namespaceName);
        sb.AppendLine("internal static partial class DbProjectionBinder");
        sb.AppendLine("{");
        foreach (var projection in projections)
        {
            AppendBindMethod(sb, projection);
        }
        AppendResolverMethod(sb, projections);
        sb.AppendLine("}");
        return sb.ToString();
    }

    internal static string EmitResolver(ImmutableArray<ProjectionInfo> projections)
    {
        var targets = projections
            .Select(static p => p.NamespaceName)
            .Distinct(StringComparer.Ordinal)
            .Select(static ns => string.IsNullOrEmpty(ns) ? "global::DbProjectionBinder" : "global::" + ns + ".DbProjectionBinder")
            .OrderBy(static t => t, StringComparer.Ordinal)
            .ToArray();

        var sb = NewSourceBuilder();
        sb.AppendLine("namespace Acta;");
        sb.AppendLine();
        sb.AppendLine("internal static class DbProjectionResolver");
        sb.AppendLine("{");
        sb.AppendLine("    public static Func<DbDataReader, T> Resolve<T>()");
        sb.AppendLine("    {");
        sb.AppendLine("        Func<DbDataReader, T>? read = null;");
        sb.AppendLine("        TryResolveGenerated(ref read);");
        sb.AppendLine(
            "        return read ?? throw new InvalidOperationException($\"No provider projection binder for '{typeof(T).FullName ?? typeof(T).Name}'.\");"
        );
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void TryResolveGenerated<T>(ref Func<DbDataReader, T>? read)");
        sb.AppendLine("    {");
        foreach (var target in targets)
        {
            sb.Append("        ").Append(target).Append('.').Append(ResolverMethodName).AppendLine("(ref read);");
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.Append(RowBindingSource);
        sb.AppendLine("}");
        return sb.ToString();
    }

    // A [DbProjection(ByName = true)] row binds by column name: the ordinals are looked up on the first row a
    // reader hands over and reused for the rest of it, so each query resolves its own binder and never shares
    // one across readers. Every other row binds by position, its columns in SELECT order.
    internal const string RowBindingSource = """
            internal static Func<DbDataReader, T> ByPosition<T>(int columns, Func<DbDataReader, int[], T> bind)
            {
                var ordinals = new int[columns];
                for (var i = 0; i < columns; i++)
                {
                    ordinals[i] = i;
                }

                return r => bind(r, ordinals);
            }

            internal static Func<DbDataReader, T> ByName<T>(string projection, string[] columns, Func<DbDataReader, int[], T> bind)
            {
                DbDataReader? reader = null;
                var ordinals = Array.Empty<int>();
                return r =>
                {
                    if (!ReferenceEquals(r, reader))
                    {
                        ordinals = Ordinals(r, projection, columns);
                        reader = r;
                    }

                    return bind(r, ordinals);
                };
            }

            private static int[] Ordinals(DbDataReader r, string projection, string[] columns)
            {
                // Each name read once: on some providers GetName is a native call.
                var actual = new string[r.FieldCount];
                for (var i = 0; i < actual.Length; i++)
                {
                    actual[i] = r.GetName(i);
                }

                var ordinals = new int[columns.Length];
                System.Collections.Generic.List<string>? missing = null;
                for (var c = 0; c < columns.Length; c++)
                {
                    ordinals[c] = -1;
                    for (var i = 0; i < actual.Length; i++)
                    {
                        if (!string.Equals(actual[i], columns[c], StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (ordinals[c] >= 0)
                        {
                            throw new InvalidOperationException($"{projection} reads column '{columns[c]}', which the result holds twice.");
                        }

                        ordinals[c] = i;
                    }

                    if (ordinals[c] < 0)
                    {
                        (missing ??= []).Add(columns[c]);
                    }
                }

                if (missing is not null)
                {
                    throw new InvalidOperationException(
                        $"{projection} reads {string.Join(", ", missing)}, which the result ({string.Join(", ", actual)}) does not have."
                    );
                }

                return ordinals;
            }

        """;

    internal static StringBuilder NewSourceBuilder()
    {
        var sb = new StringBuilder();
        GeneratorText.AutoGeneratedHeader(sb);
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Data.Common;");
        sb.AppendLine("using System.Globalization;");
        sb.AppendLine("using Acta;");
        sb.AppendLine();
        return sb;
    }

    internal static void AppendNamespace(StringBuilder sb, string namespaceName)
    {
        if (!string.IsNullOrEmpty(namespaceName))
        {
            sb.Append("namespace ").Append(namespaceName).AppendLine(";");
            sb.AppendLine();
        }
    }

    internal static void AppendBindMethod(StringBuilder sb, ProjectionInfo projection)
    {
        var columns = projection.Members.Select(static m => "\"" + m.Column + "\"");
        sb.Append("    internal static readonly string[] ")
            .Append(projection.ColumnsName)
            .Append(" = [")
            .Append(string.Join(", ", columns))
            .AppendLine("];");
        sb.AppendLine();
        sb.Append("    public static ")
            .Append(projection.TypeFqn)
            .Append(' ')
            .Append(projection.MethodName)
            .AppendLine("(DbDataReader r, int[] o) =>");

        if (projection.Members.Length == 0)
        {
            sb.AppendLine("        new();");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("        new(");
        for (var i = 0; i < projection.Members.Length; i++)
        {
            var member = projection.Members[i];
            sb.Append("            ").Append(GeneratorText.EscapeIdentifier(member.Name)).Append(": ").Append(BindExpression(member, i));
            sb.AppendLine(i == projection.Members.Length - 1 ? "" : ",");
        }
        sb.AppendLine("        );");
        sb.AppendLine();
    }

    internal static void AppendResolverMethod(StringBuilder sb, ImmutableArray<ProjectionInfo> projections)
    {
        sb.Append("    internal static void ").Append(ResolverMethodName).AppendLine("<T>(ref Func<DbDataReader, T>? read)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (read is not null)");
        sb.AppendLine("        {");
        sb.AppendLine("            return;");
        sb.AppendLine("        }");
        sb.AppendLine();

        foreach (var projection in projections)
        {
            sb.Append("        if (typeof(T) == typeof(").Append(projection.TypeFqn).AppendLine("))");
            sb.AppendLine("        {");
            sb.Append("            read = (Func<DbDataReader, T>)(object)global::Acta.DbProjectionResolver.")
                .Append(projection.ByName ? "ByName<" : "ByPosition<")
                .Append(projection.TypeFqn)
                .Append(projection.ByName ? ">(\"" + projection.TypeName + "\", " : ">(")
                .Append(projection.ColumnsName)
                .Append(projection.ByName ? ", " : ".Length, ")
                .Append(projection.MethodName)
                .AppendLine(");");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    // `column` indexes the projection's column list; o[column] is that column's ordinal in the reader.
    internal static string BindExpression(ProjectionMemberInfo member, int column)
    {
        var ord = "o[" + column.ToString(CultureInfo.InvariantCulture) + "]";
        var typed = BindTypedExpression(member, ord);
        return member.IsNullable ? "r.IsDBNull(" + ord + ") ? null : " + typed : typed;
    }

    internal static string BindTypedExpression(ProjectionMemberInfo member, string ord)
    {
        var readExpr = member.Kind switch
        {
            ProjectionMemberKind.Boolean => "Convert.ToBoolean(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Byte => "Convert.ToByte(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Int32 => "Convert.ToInt32(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Int64 => "Convert.ToInt64(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Guid => "r.GetGuid(" + ord + ")",
            ProjectionMemberKind.Ref => "new " + member.RefTypeFqn + "(r.GetGuid(" + ord + "))",
            ProjectionMemberKind.UtcInstant => "r.GetDateTimeUtc(" + ord + ")",
            ProjectionMemberKind.String => "r.GetString(" + ord + ")",
            ProjectionMemberKind.Bytes => "(byte[])r.GetValue(" + ord + ")",
            ProjectionMemberKind.ReadOnlyMemoryBytes => "(byte[])r.GetValue(" + ord + ")",
            _ => throw new InvalidOperationException($"Unsupported projection member kind {member.Kind}."),
        };

        if (member.DecoderFqn is not null)
        {
            return member.DecoderFqn + ".FromId(" + readExpr + ")";
        }

        return member.EnumTypeFqn is null ? readExpr : "(" + member.EnumTypeFqn + ")" + readExpr;
    }

    internal static string HintPart(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "Global";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return sb.ToString();
    }
}
