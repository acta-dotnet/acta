using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
            AppendBindMethod(sb, projection, "    ");
        }
        AppendResolverMethod(sb, projections, "    ", binderPrefix: "");
        sb.AppendLine("}");
        return sb.ToString();
    }

    internal static string EmitNestedBinder(ImmutableArray<ProjectionInfo> projections)
    {
        var first = projections[0];
        var sb = NewSourceBuilder();
        AppendNamespace(sb, first.NamespaceName);

        var indent = "";
        foreach (var container in first.Containers)
        {
            sb.Append(indent);
            if (!string.IsNullOrEmpty(container.Accessibility))
            {
                sb.Append(container.Accessibility).Append(' ');
            }
            if (container.IsStatic)
            {
                sb.Append("static ");
            }
            sb.Append("partial ").Append(container.Kind).Append(' ').Append(container.Name).AppendLine();
            sb.Append(indent).AppendLine("{");
            indent += "    ";
        }

        sb.Append(indent).AppendLine("private static partial class DbProjectionBinder");
        sb.Append(indent).AppendLine("{");
        foreach (var projection in projections.OrderBy(static p => p.MethodName, StringComparer.Ordinal))
        {
            AppendBindMethod(sb, projection, indent + "    ");
        }
        sb.Append(indent).AppendLine("}");
        sb.AppendLine();
        AppendResolverMethod(sb, projections, indent, binderPrefix: "DbProjectionBinder.");

        for (var i = first.Containers.Length - 1; i >= 0; i--)
        {
            indent = indent.Substring(0, indent.Length - 4);
            sb.Append(indent).AppendLine("}");
        }

        return sb.ToString();
    }

    internal static string EmitResolver(ImmutableArray<ProjectionInfo> projections)
    {
        var namespaceTargets = projections
            .Where(static p => p.Containers.IsDefaultOrEmpty)
            .GroupBy(static p => p.NamespaceName)
            .Select(static g => string.IsNullOrEmpty(g.Key) ? "global::DbProjectionBinder" : "global::" + g.Key + ".DbProjectionBinder");

        var nestedTargets = projections
            .Where(static p => !p.Containers.IsDefaultOrEmpty)
            .GroupBy(static p => p.ContainerKey)
            .Select(static g => g.First().ContainerFqn);

        var targets = namespaceTargets.Concat(nestedTargets).OrderBy(static t => t, StringComparer.Ordinal).ToArray();

        var sb = NewSourceBuilder();
        sb.AppendLine("namespace Acta.Runtime;");
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
        sb.AppendLine("using Acta.Runtime;");
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

    internal static void AppendBindMethod(StringBuilder sb, ProjectionInfo projection, string indent)
    {
        var columns = projection.Members.SelectMany(static m => m.Columns).Select(static c => "\"" + c + "\"");
        sb.Append(indent)
            .Append("internal static readonly string[] ")
            .Append(projection.ColumnsName)
            .Append(" = [")
            .Append(string.Join(", ", columns))
            .AppendLine("];");
        sb.AppendLine();
        sb.Append(indent)
            .Append("public static ")
            .Append(projection.TypeFqn)
            .Append(' ')
            .Append(projection.MethodName)
            .AppendLine("(DbDataReader r, int[] o) =>");

        if (projection.Members.Length == 0)
        {
            sb.Append(indent).AppendLine("    new();");
            sb.AppendLine();
            return;
        }

        sb.Append(indent).AppendLine("    new(");
        var column = 0;
        for (var i = 0; i < projection.Members.Length; i++)
        {
            var member = projection.Members[i];
            sb.Append(indent).Append("        ").Append(EscapeIdentifier(member.Name)).Append(": ").Append(BindExpression(member, column));
            sb.AppendLine(i == projection.Members.Length - 1 ? "" : ",");
            column += member.Columns.Length;
        }
        sb.Append(indent).AppendLine("    );");
        sb.AppendLine();
    }

    internal static void AppendResolverMethod(
        StringBuilder sb,
        ImmutableArray<ProjectionInfo> projections,
        string indent,
        string binderPrefix
    )
    {
        sb.Append(indent).Append("internal static void ").Append(ResolverMethodName).AppendLine("<T>(ref Func<DbDataReader, T>? read)");
        sb.Append(indent).AppendLine("{");
        sb.Append(indent).AppendLine("    if (read is not null)");
        sb.Append(indent).AppendLine("    {");
        sb.Append(indent).AppendLine("        return;");
        sb.Append(indent).AppendLine("    }");
        sb.AppendLine();

        foreach (var projection in projections.OrderBy(static p => p.MethodName, StringComparer.Ordinal))
        {
            sb.Append(indent).Append("    if (typeof(T) == typeof(").Append(projection.TypeFqn).AppendLine("))");
            sb.Append(indent).AppendLine("    {");
            sb.Append(indent)
                .Append("        read = (Func<DbDataReader, T>)(object)global::Acta.Runtime.DbProjectionResolver.")
                .Append(projection.ByName ? "ByName<" : "ByPosition<")
                .Append(projection.TypeFqn)
                .Append(projection.ByName ? ">(\"" + projection.TypeName + "\", " : ">(")
                .Append(binderPrefix)
                .Append(projection.ColumnsName)
                .Append(projection.ByName ? ", " : ".Length, ")
                .Append(binderPrefix)
                .Append(projection.MethodName)
                .AppendLine(");");
            sb.Append(indent).AppendLine("        return;");
            sb.Append(indent).AppendLine("    }");
            sb.AppendLine();
        }

        sb.Append(indent).AppendLine("}");
        sb.AppendLine();
    }

    // `column` indexes the projection's column list; o[column] is that column's ordinal in the reader.
    internal static string BindExpression(ProjectionMemberInfo member, int column)
    {
        var ord = "o[" + column.ToString(CultureInfo.InvariantCulture) + "]";
        if (member.Kind == ProjectionMemberKind.Payload)
        {
            // A payload is its format id and its bytes; format id 0 is no payload, null when the member allows it.
            var bytes = "o[" + (column + 1).ToString(CultureInfo.InvariantCulture) + "]";
            var formatId = "Convert.ToByte(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)";
            var payload =
                "global::Acta.JobPayload.FromBytes(global::Acta.JobPayloadFormat.ForId("
                + formatId
                + "), r.IsDBNull("
                + bytes
                + ") ? [] : (byte[])r.GetValue("
                + bytes
                + "))";
            return member.IsNullable ? "r.IsDBNull(" + ord + ") || " + formatId + " == 0 ? null : " + payload : payload;
        }

        var typed = BindTypedExpression(member, ord);
        return member.IsNullable ? "r.IsDBNull(" + ord + ") ? null : " + typed : typed;
    }

    internal static string BindTypedExpression(ProjectionMemberInfo member, string ord)
    {
        var readExpr = member.Kind switch
        {
            ProjectionMemberKind.Boolean => "Convert.ToBoolean(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Byte => "Convert.ToByte(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.SByte => "Convert.ToSByte(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Int16 => "Convert.ToInt16(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.UInt16 => "Convert.ToUInt16(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Int32 => "Convert.ToInt32(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.UInt32 => "Convert.ToUInt32(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Int64 => "Convert.ToInt64(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.UInt64 => "Convert.ToUInt64(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.Guid => "r.GetGuid(" + ord + ")",
            ProjectionMemberKind.Ref => "new " + member.RefTypeFqn + "(r.GetGuid(" + ord + "))",
            ProjectionMemberKind.UtcInstant => "r.GetDateTimeUtc(" + ord + ")",
            ProjectionMemberKind.Decimal => "Convert.ToDecimal(r.GetValue(" + ord + "), CultureInfo.InvariantCulture)",
            ProjectionMemberKind.String => "r.GetString(" + ord + ")",
            ProjectionMemberKind.Bytes => "(byte[])r.GetValue(" + ord + ")",
            ProjectionMemberKind.ReadOnlyMemoryBytes => "(byte[])r.GetValue(" + ord + ")",
            ProjectionMemberKind.MemoryBytes => "(byte[])r.GetValue(" + ord + ")",
            _ => throw new InvalidOperationException($"Unsupported projection member kind {member.Kind}."),
        };

        if (member.DecoderFqn is not null)
        {
            return member.DecoderFqn + ".FromId(" + readExpr + ")";
        }

        return member.EnumTypeFqn is null ? readExpr : "(" + member.EnumTypeFqn + ")" + readExpr;
    }

    internal static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None && SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.None
            ? name
            : "@" + name;

    internal static string AccessibilityKeyword(Accessibility accessibility) =>
        accessibility switch
        {
            Accessibility.Private => "private",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.Public => "public",
            _ => "",
        };

    internal static string TypeDeclarationKeyword(INamedTypeSymbol type) =>
        type.TypeKind switch
        {
            TypeKind.Struct when type.IsRecord => "record struct",
            TypeKind.Struct => "struct",
            TypeKind.Class when type.IsRecord => "record",
            _ => "class",
        };

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
