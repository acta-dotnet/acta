using Acta.Tests.Conformance.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Acta.Tests.Architecture;

/// <summary>
/// The standing evidence behind the permanent CA2100 suppression in <c>Directory.Build.props</c>.
/// The analyzer is switched off because no <c>CommandText</c> in the shipped libraries is built from
/// runtime input; a suppression whose premise nothing re-checks is only true on the day it is
/// written, so this walks every <c>src/</c> source file and re-checks it on every build.
/// <para>
/// The rule: an assignment to <c>CommandText</c> must be an embedded-resource load, or text made
/// only of compile-time literals. Anything else - an interpolation with a hole, a concatenation with
/// a non-literal operand, a <see cref="System.Text.StringBuilder"/>, a value whose origin the file
/// does not show - is a composition, and every composition is declared one by one below with the
/// identifier validation that makes it safe. A new composed site fails until someone reads it and
/// either removes the composition or declares why it cannot carry a value.
/// </para>
/// <para>
/// This is a Roslyn syntax walk rather than a text scan because the distinction it has to draw is
/// syntactic, not lexical: <c>$"..."</c> with a hole and <c>$"..."</c> without one are the same
/// characters to a regex, raw string literals carry arbitrary SQL including quotes and braces, and
/// a pattern loose enough to catch <c>sb.ToString()</c> would also catch the safe forms. A scan that
/// can pass something unsafe is not a guard. <c>Microsoft.CodeAnalysis.CSharp</c> is already
/// referenced by this project for the generator tests, so this costs no new dependency.
/// </para>
/// </summary>
public sealed class CommandTextCompositionTests
{
    /// <summary>
    /// Methods that return embedded SQL. <c>SqlResourceCatalog.Load</c> reads an
    /// <c>Sql/{Module}/.../{Operation}.sql</c> resource compiled into the provider assembly, so its
    /// result is authored text with no runtime component beyond the substitution tokens the catalog
    /// renders - and those are the validated identifiers pinned by the second test below.
    /// </summary>
    private static readonly string[] EmbeddedSqlMethods = ["Load"];

    /// <summary>
    /// Every member in <c>src/</c> that composes <c>CommandText</c> rather than loading or quoting
    /// it, the number of such assignments it makes, and the guard that makes each one safe. Keyed by
    /// <c>{path under src}::{member}</c>: an extra composed assignment inside a declared member
    /// fails on the count, a composed assignment anywhere else fails as undeclared, and a
    /// declaration whose member stops composing fails as stale.
    /// </summary>
    private static readonly Dictionary<string, (int Sites, string Guard)> DeclaredCompositions = new(StringComparer.Ordinal)
    {
        // The routine-call shape. Both dialects interpolate the schema and the routine name into a
        // CALL/SELECT header and nothing else: the schema is rejected at startup unless it is a bare
        // identifier (SqlProviderOptionsValidator), the routine name is validated on the line above
        // the assignment, and the Postgres argument list is built from the command's own already-bound
        // DbParameter names, so the values travel as parameters and never as text.
        ["Acta.Postgres/Services/PostgresDialect.cs::ConfigureRoutineCommand"] = (
            1,
            "schema + routine name, both bare-identifier validated"
        ),
        ["Acta.SqlServer/Services/SqlServerDialect.cs::ConfigureRoutineCommand"] = (
            1,
            "schema + routine name, both bare-identifier validated"
        ),
        // The dev-convenience bootstrap DDL. CREATE DATABASE and ALTER DATABASE take no parameters on
        // either provider, so the database name is interpolated - into a quoted identifier on PG, and
        // into both a bracket identifier and an N'...' literal on SQL Server. ValidateDatabaseName
        // rejects the quote, bracket and apostrophe characters that would break out of any of those
        // contexts, and runs before the connection is opened.
        ["Acta.Postgres/Schema/PostgresSchemaMigrator.cs::EnsureDatabaseAndApplyAsync"] = (1, "database name, ValidateDatabaseName"),
        ["Acta.SqlServer/Schema/SqlServerSchemaMigrator.cs::EnsureDatabaseAndApplyAsync"] = (2, "database name, ValidateDatabaseName"),
        // The SQLite test-reset teardown. SQLite has no DROP SCHEMA CASCADE, so the object names come
        // from sqlite_master - the database's own catalog, not a caller - and each is emitted inside a
        // double-quoted identifier with any embedded quote doubled.
        ["Acta.Sqlite/Schema/SqliteSchemaMigrator.cs::DropAllAsync"] = (1, "sqlite_master object names, quote-doubled"),
        // Per-connection PRAGMAs. The one interpolated hole is NORMAL or FULL, picked from the
        // ExecutionProfile enum in the constructor; there is no third value it can hold.
        ["Acta.Sqlite/Services/SqliteDialect.cs::OnStateChange"] = (1, "literal PRAGMA text, enum-chosen synchronous mode"),
        // Migration and object installation. The text is an embedded DDL resource (M-numbered
        // migration scripts, provider view and routine bodies) split into batches by the provider
        // hooks; the only substitution is {{schema}}, and the CREATE VIEW wrapper's qualified name is
        // that same schema plus a name derived from the resource file name.
        ["Acta.Relational/Schema/SchemaMigrationRunner.cs::ApplyAsync"] = (2, "embedded migration/prelude DDL, {{schema}} only"),
        ["Acta.Relational/Schema/SqlObjectInstaller.cs::Run"] = (1, "embedded view/routine DDL batch"),
        ["Acta.Relational/Schema/SqlObjectInstaller.cs::CurrentDefinition"] = (1, "provider-owned hook SQL, name bound as @p_name"),
        // The outbox relay's two counters, the only relay commands whose SQL is identical on all
        // three providers and therefore composed inline instead of shipped as resources. The single
        // hole is the qualified table reference, produced by OutboxIdentifier.Qualify, which
        // bare-identifier validates both the schema and the table before joining them.
        ["Acta.Relational/Stores/RelationalOutboxRelayStore.cs::CountBacklogAsync"] = (1, "table ref, OutboxIdentifier.Qualify"),
        ["Acta.Relational/Stores/RelationalOutboxRelayStore.cs::CountQuarantinedAsync"] = (1, "table ref, OutboxIdentifier.Qualify"),
        // The three provider staging extensions. The INSERT text is built once by OutboxStaging.Prepare
        // over an OutboxIdentifier.Qualify'd table reference and handed here as a parameter; every
        // column value is added as a provider DbParameter in the lines below the assignment.
        ["Acta.Postgres/PostgresOutboxStagingExtensions.cs::InsertAsync"] = (1, "shared staging INSERT, table ref validated"),
        ["Acta.SqlServer/SqlServerOutboxStagingExtensions.cs::InsertAsync"] = (1, "shared staging INSERT, table ref validated"),
        ["Acta.Sqlite/SqliteOutboxStagingExtensions.cs::InsertAsync"] = (1, "shared staging INSERT, table ref validated"),
        // Acta.Testing is the spec-authoring seam and composes on purpose: DbFrom builds its SELECT /
        // UPDATE / DELETE / INSERT from generated DbEntitySpec column metadata, and ExecuteRawAsync
        // executes SQL the test author wrote. Neither has an untrusted caller - the caller is the
        // test - and both bind every value through DbValueCoercion as a DbParameter, so no value is
        // ever concatenated into the text even here.
        ["Acta.Testing/Relational/Querying/DbFrom.cs::CountAsync"] = (1, "generated column metadata, values parameterized"),
        ["Acta.Testing/Relational/Querying/DbFrom.cs::UpdateOnlyAsync"] = (1, "generated column metadata, values parameterized"),
        ["Acta.Testing/Relational/Querying/DbFrom.cs::DeleteAsync"] = (1, "generated column metadata, values parameterized"),
        ["Acta.Testing/Relational/Querying/DbFrom.cs::InsertCoreAsync"] = (1, "generated column metadata, values parameterized"),
        ["Acta.Testing/Relational/Querying/DbFrom.cs::BuildCommand"] = (1, "generated column metadata, values parameterized"),
        ["Acta.Testing/Relational/TestConnectionExtensions.cs::ExecuteRawAsync"] = (1, "spec-authored raw SQL, {schema} substitution only"),
    };

    [Fact(DisplayName = "Every src CommandText assignment loads embedded SQL, is literal, or is a declared composition")]
    public void Src_command_text_is_never_built_from_runtime_input()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = IntegrationConfig.FindRepoRoot();
        var srcRoot = Path.Combine(root, "src");
        var composed = new List<Site>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file, cancellationToken: ct);
            var relative = Path.GetRelativePath(srcRoot, file).Replace(Path.DirectorySeparatorChar, '/');

            foreach (var assignment in tree.GetRoot(ct).DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!AssignsCommandText(assignment.Left))
                {
                    continue;
                }

                scanned++;
                var member = EnclosingMember(assignment);
                if (IsEmbeddedOrLiteral(assignment.Right, member, resolve: true))
                {
                    continue;
                }

                var position = assignment.GetLocation().GetLineSpan().StartLinePosition;
                composed.Add(
                    new Site(
                        Key: $"{relative}::{MemberName(member)}",
                        Where: $"{relative}({position.Line + 1},{position.Character + 1})",
                        Expression: Condense(assignment.Right.ToString())
                    )
                );
            }
        }

        // A src tree with no CommandText at all would pass vacuously, which is the one way this gate
        // can rot into a no-op without anyone noticing.
        Assert.True(scanned > 0, $"No CommandText assignment found under {srcRoot}: the scan is broken, not the code.");

        var failures = new List<string>();
        var found = composed
            .GroupBy(static s => s.Key, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);

        foreach (var (key, sites) in found.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (!DeclaredCompositions.TryGetValue(key, out var declared))
            {
                foreach (var site in sites)
                {
                    failures.Add(
                        $"{site.Where}: CommandText is composed, not loaded or literal: `{site.Expression}`. "
                            + "Load the SQL from an embedded Sql/ resource, or declare this member in DeclaredCompositions "
                            + "with the identifier validation that keeps it injection-safe."
                    );
                }

                continue;
            }

            if (declared.Sites != sites.Count)
            {
                failures.Add(
                    $"{key}: declared {declared.Sites} composed CommandText assignment(s) ({declared.Guard}), found {sites.Count}: "
                        + string.Join("; ", sites.Select(static s => $"{s.Where} `{s.Expression}`"))
                        + ". Read the new one before changing the count."
                );
            }
        }

        foreach (var key in DeclaredCompositions.Keys.Where(k => !found.ContainsKey(k)).OrderBy(static k => k, StringComparer.Ordinal))
        {
            failures.Add($"{key}: declared as composing CommandText but no longer does. Delete the declaration.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The identifier validation the declared compositions above rest on. Every one of them
    /// interpolates a schema, table, routine or database name; each of those names is rejected at its
    /// own boundary unless it matches a bare-identifier shape with no SQL delimiter characters in it.
    /// If one of these calls is deleted the compositions become injectable, so the CA2100 suppression
    /// must fall with it.
    /// </summary>
    [Fact(DisplayName = "The identifier validation the composed CommandText sites rest on is still wired")]
    public void Composed_identifiers_are_still_validated_at_their_boundary()
    {
        var root = IntegrationConfig.FindRepoRoot();
        (string Path, string Call)[] pins =
        [
            // The Schema every {{schema}} substitution and every routine header uses, rejected at
            // host startup by ValidateOnStart before a single command is built.
            ("src/Acta.Relational/Connections/SqlProviderOptionsValidator.cs", "IdentifierSyntax.IsBareIdentifier(options.Schema)"),
            // The external-outbox schema and table, the only physical names an application chooses.
            ("src/Acta.Runtime/Hosting/OutboxIdentifier.cs", "IdentifierSyntax.ValidateBareIdentifier(value, kind)"),
            // The routine name each dialect interpolates into its call header.
            ("src/Acta.Postgres/Services/PostgresDialect.cs", "IdentifierSyntax.ValidateBareIdentifier(routineName"),
            ("src/Acta.SqlServer/Services/SqlServerDialect.cs", "IdentifierSyntax.ValidateBareIdentifier(routineName"),
            // The database name the bootstrap DDL interpolates into quoted and bracketed contexts.
            ("src/Acta.Postgres/Schema/PostgresSchemaMigrator.cs", "IdentifierSyntax.ValidateDatabaseName(databaseName"),
            ("src/Acta.SqlServer/Schema/SqlServerSchemaMigrator.cs", "IdentifierSyntax.ValidateDatabaseName(databaseName"),
        ];

        var failures = new List<string>();
        foreach (var (path, call) in pins)
        {
            var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                failures.Add($"{path}: missing; the CA2100 suppression names it as a guard.");
                continue;
            }

            if (!File.ReadAllText(file).Contains(call, StringComparison.Ordinal))
            {
                failures.Add($"{path}: no longer calls `{call}`, which the CA2100 suppression relies on.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private sealed record Site(string Key, string Where, string Expression);

    private static bool AssignsCommandText(ExpressionSyntax target) =>
        target switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText == "CommandText",
            // The object-initializer form: new SqlCommand { CommandText = ... }.
            IdentifierNameSyntax i => i.Identifier.ValueText == "CommandText",
            _ => false,
        };

    /// <summary>
    /// True when <paramref name="expression"/> is an embedded-resource load or text the compiler
    /// already knows in full. Everything else is a composition. <paramref name="resolve"/> allows one
    /// hop through a local or constant declared in the same member or type, so
    /// <c>var sql = _sql.Load(path); cmd.CommandText = sql;</c> reads as the load it is; the hop is
    /// not taken twice, and never across a method boundary, because a syntax tree cannot see what a
    /// caller passed in - which is exactly why those sites are declared instead.
    /// </summary>
    private static bool IsEmbeddedOrLiteral(ExpressionSyntax? expression, SyntaxNode? member, bool resolve) =>
        expression switch
        {
            null => false,
            ParenthesizedExpressionSyntax p => IsEmbeddedOrLiteral(p.Expression, member, resolve),
            PostfixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.SuppressNullableWarningExpression) => IsEmbeddedOrLiteral(
                u.Operand,
                member,
                resolve
            ),
            LiteralExpressionSyntax => true,
            // An interpolated string is literal only when it has no holes: a raw or verbatim string
            // written with $ but interpolating nothing.
            InterpolatedStringExpressionSyntax s => s.Contents.All(static c => c is InterpolatedStringTextSyntax),
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) => IsEmbeddedOrLiteral(b.Left, member, resolve)
                && IsEmbeddedOrLiteral(b.Right, member, resolve),
            ConditionalExpressionSyntax c => IsEmbeddedOrLiteral(c.WhenTrue, member, resolve)
                && IsEmbeddedOrLiteral(c.WhenFalse, member, resolve),
            InvocationExpressionSyntax i => EmbeddedSqlMethods.Contains(InvokedName(i), StringComparer.Ordinal),
            IdentifierNameSyntax name when resolve => ResolvesToEmbeddedOrLiteral(name.Identifier.ValueText, member),
            _ => false,
        };

    private static string InvokedName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            IdentifierNameSyntax i => i.Identifier.ValueText,
            _ => "",
        };

    private static bool ResolvesToEmbeddedOrLiteral(string name, SyntaxNode? member)
    {
        var local = member
            ?.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == name && v.Initializer is not null);
        if (local is not null)
        {
            return IsEmbeddedOrLiteral(local.Initializer!.Value, member, resolve: false);
        }

        // A const or static readonly field of the enclosing type, whose initializer is right there.
        var field = member
            ?.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault()
            ?.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword) || f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            .SelectMany(static f => f.Declaration.Variables)
            .FirstOrDefault(v => v.Identifier.ValueText == name && v.Initializer is not null);

        return field is not null && IsEmbeddedOrLiteral(field.Initializer!.Value, member, resolve: false);
    }

    private static SyntaxNode? EnclosingMember(SyntaxNode node) =>
        node.Ancestors()
            .FirstOrDefault(static a => a is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or LocalFunctionStatementSyntax);

    private static string MemberName(SyntaxNode? member) =>
        member switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            LocalFunctionStatementSyntax l => l.Identifier.ValueText,
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            ConstructorDeclarationSyntax c => c.Identifier.ValueText + ".ctor",
            _ => "<file>",
        };

    /// <summary>Collapse an expression onto one line so the failure message stays readable when the
    /// offending text is a raw string literal or a multi-line builder call.</summary>
    private static string Condense(string expression)
    {
        var single = string.Join(
            ' ',
            expression.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(static p => p.Trim())
        );
        return single.Length <= 160 ? single : single[..157] + "...";
    }

    private static bool IsBuildArtifact(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
