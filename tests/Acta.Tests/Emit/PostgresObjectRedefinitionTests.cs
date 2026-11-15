using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// The published PostgreSQL script runs every migration on every run, and <c>CREATE OR REPLACE</c> cannot take a column
/// from a view or change a function's parameters or result, so a second run would fail at an earlier definition. The
/// script emitter publishes each earlier definition of a view behind a guard on the view's absence (CONTRIBUTING.md,
/// "Schema migrations"). Nothing guards a routine: one redefined by a later migration keeps its parameters and result,
/// and a second run replays the earlier body and then the later one.
/// </summary>
public sealed partial class PostgresObjectRedefinitionTests
{
    [Fact]
    public void No_routine_header_is_redefined_by_a_later_migration()
    {
        var dir = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Postgres", "Migrations");
        var defined = new List<(string Name, string Header, string File)>();
        foreach (var path in Directory.GetFiles(dir, "*.sql").Order(StringComparer.Ordinal))
        {
            var sql = File.ReadAllText(path);
            var routines = Routine().Matches(sql);

            // A routine whose parameters and result end anywhere but a LANGUAGE or AS line would go unread.
            Assert.Equal(RoutineStart().Count(sql), routines.Count);
            defined.AddRange(
                routines.Select(m =>
                    (m.Groups["name"].Value, Whitespace().Replace(m.Groups["header"].Value, " ").Trim(), Path.GetFileName(path))
                )
            );
        }

        // The init defines every routine, so an empty read means the patterns stopped matching.
        Assert.NotEmpty(defined);
        var redefined = defined
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .Where(g => g.Select(d => d.Header).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{g.Key} in {string.Join(", ", g.Select(d => d.File).Distinct())}")
            .ToArray();
        Assert.True(
            redefined.Length == 0,
            "A PostgreSQL routine's parameters or result are defined by more than one migration, which the published script "
                + "cannot run twice: "
                + string.Join("; ", redefined)
        );
    }

    [Fact]
    public void The_published_script_defines_each_view_once_unguarded()
    {
        var script = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "docs", "reference", "schema-pg.sql"));
        var views = PublishedView().Matches(script).Select(m => m.Groups["name"].Value).ToList();

        Assert.NotEmpty(views);
        var repeated = views.GroupBy(name => name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.True(
            repeated.Length == 0,
            "A PostgreSQL view has more than one unguarded definition in the published script, which cannot run twice: "
                + string.Join(", ", repeated)
        );
    }

    [GeneratedRegex(
        @"^CREATE OR REPLACE (?:FUNCTION|PROCEDURE) \{\{schema\}\}\.(?<name>\w+)(?<header>[\s\S]*?)^(?:LANGUAGE|AS)\b",
        RegexOptions.Multiline | RegexOptions.CultureInvariant
    )]
    private static partial Regex Routine();

    [GeneratedRegex(@"^CREATE OR REPLACE (?:FUNCTION|PROCEDURE) ", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RoutineStart();

    // A guarded definition is an indented CREATE VIEW inside its DO block, so only an unguarded one starts a line.
    [GeneratedRegex(@"^CREATE OR REPLACE VIEW \w+\.(?<name>\w+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PublishedView();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
