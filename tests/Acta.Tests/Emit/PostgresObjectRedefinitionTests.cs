using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// The published PostgreSQL script runs every migration on every run, and <c>CREATE OR REPLACE</c> cannot take a column
/// from a view or change a function's result. Once a migration redefines a view or routine that an earlier one defined,
/// a second run of the script fails at the earlier definition. The first migration that does so fails here until the
/// script emitter guards the earlier definition (CONTRIBUTING.md, "Schema migrations").
/// </summary>
public sealed partial class PostgresObjectRedefinitionTests
{
    [Fact]
    public void No_view_or_routine_is_defined_by_two_migrations()
    {
        var dir = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Postgres", "Migrations");
        var defined = Directory
            .GetFiles(dir, "*.sql")
            .SelectMany(file =>
                Definition().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value).Distinct().Select(name => (name, file))
            )
            .ToList();

        // The init defines every view and routine, so an empty read means the pattern stopped matching.
        Assert.NotEmpty(defined);
        var redefined = defined
            .GroupBy(d => d.name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} in {string.Join(", ", g.Select(d => Path.GetFileName(d.file)))}")
            .ToArray();
        Assert.True(
            redefined.Length == 0,
            "A PostgreSQL view or routine is defined by more than one migration, which the published script cannot run twice: "
                + string.Join("; ", redefined)
        );
    }

    [GeneratedRegex(
        @"^CREATE OR REPLACE (?:VIEW|FUNCTION|PROCEDURE) \{\{schema\}\}\.(\w+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant
    )]
    private static partial Regex Definition();
}
