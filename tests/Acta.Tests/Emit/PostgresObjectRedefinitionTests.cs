using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// The published PostgreSQL script runs every migration on every run, and <c>CREATE OR REPLACE</c> cannot take a column
/// from a view or change a function's parameters or result. Once a migration redefines a view, or a routine's parameters
/// or result, a second run of the script fails at the earlier definition, and the first migration that does so fails here
/// until the script emitter guards the earlier definition (CONTRIBUTING.md, "Schema migrations"). A routine redefined with
/// the same parameters and result needs no guard: a second run replays the earlier body and then the later one.
/// </summary>
public sealed partial class PostgresObjectRedefinitionTests
{
    [Fact]
    public void No_view_or_routine_header_is_redefined_by_a_later_migration()
    {
        var dir = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Postgres", "Migrations");
        var defined = new List<(string Name, string Header, string File)>();
        foreach (var path in Directory.GetFiles(dir, "*.sql").Order(StringComparer.Ordinal))
        {
            var sql = File.ReadAllText(path);
            var file = Path.GetFileName(path);
            var routines = Routine().Matches(sql);

            // A routine whose parameters and result end anywhere but a LANGUAGE or AS line would go unread.
            Assert.Equal(RoutineStart().Count(sql), routines.Count);
            defined.AddRange(
                routines.Select(m => (m.Groups["name"].Value, Whitespace().Replace(m.Groups["header"].Value, " ").Trim(), file))
            );

            // A view's columns are not readable here, so every definition of one counts as a new header.
            defined.AddRange(View().Matches(sql).Select(m => (m.Groups["name"].Value, file, file)));
        }

        // The init defines every view and routine, so an empty read means the patterns stopped matching.
        Assert.NotEmpty(defined);
        var redefined = defined
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .Where(g => g.Select(d => d.Header).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"{g.Key} in {string.Join(", ", g.Select(d => d.File).Distinct())}")
            .ToArray();
        Assert.True(
            redefined.Length == 0,
            "A PostgreSQL view, or a routine's parameters or result, is defined by more than one migration, which the published "
                + "script cannot run twice: "
                + string.Join("; ", redefined)
        );
    }

    [GeneratedRegex(
        @"^CREATE OR REPLACE (?:FUNCTION|PROCEDURE) \{\{schema\}\}\.(?<name>\w+)(?<header>[\s\S]*?)^(?:LANGUAGE|AS)\b",
        RegexOptions.Multiline | RegexOptions.CultureInvariant
    )]
    private static partial Regex Routine();

    [GeneratedRegex(@"^CREATE OR REPLACE (?:FUNCTION|PROCEDURE) ", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RoutineStart();

    [GeneratedRegex(@"^CREATE OR REPLACE VIEW \{\{schema\}\}\.(?<name>\w+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex View();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
