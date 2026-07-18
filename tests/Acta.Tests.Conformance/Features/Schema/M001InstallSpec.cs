using Acta.Relational.Schema;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schema;

/// <summary>
/// Conformance: applying M001 to a fresh schema installs exactly the modelled entity tables.
/// </summary>
[ConformanceSpec(
    "schema.m001-install",
    "M001 installs exactly the modelled entity tables",
    Area = "Schema",
    Contract = "Applying M001 to a fresh schema installs exactly the modelled entity tables.",
    Arrange = "A fresh empty schema is allocated by the fixture.",
    Act = "The M001 migration is applied to the fresh schema.",
    Assert = "The installed base tables, columns, indexes, and constraints exactly match the ActaSchema entity set."
)]
public abstract class M001InstallSpec<TFixture> : IntegrationSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    // The migration runner's own bookkeeping ledger (created by the runner's EnsureMigrations step)
    // is not an entity in the model, so it is the one base table permitted beyond the entity set.
    private const string MigrationHistoryTable = "migrations";

    [Fact(DisplayName = "Schema base tables equal the ActaSchema entity set with nothing missing or extra")]
    public async Task InstallsExactlyTheModelledEntityTables()
    {
        var expected = ActaSchema.Entities.Select(e => e.TableName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var actual = (await Fixture.ListTablesAsync(Schema.SchemaName))
            .Where(t => t != MigrationHistoryTable)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"Schema table mismatch. Missing: [{string.Join(", ", missing)}] Extra: [{string.Join(", ", extra)}]"
        );
    }

    [Fact(DisplayName = "Each installed table's columns equal the modelled columns")]
    public async Task InstallsExactlyTheModelledColumns()
    {
        // Round-trip: applying the whole committed migration history must reconstruct the model's
        // columns per table — the backstop against a hand-edited delta drifting from ActaSchema.
        foreach (var entity in ActaSchema.Entities)
        {
            var cols = await Fixture.ListColumnsAsync(Schema.SchemaName, entity.TableName);

            // 1. Exact column-name set (presence), all columns including PK and generated.
            var expectedNames = entity.Columns.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var actualNames = cols.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var missing = expectedNames.Except(actualNames).ToList();
            var extra = actualNames.Except(expectedNames).ToList();
            Assert.True(
                missing.Count == 0 && extra.Count == 0,
                $"Column mismatch on {entity.TableName}. Missing: [{string.Join(", ", missing)}] Extra: [{string.Join(", ", extra)}]"
            );

            // 2. Nullability of plain columns. PK columns (provider-specific rowid/identity reporting)
            // and generated columns (computed-nullability reporting varies) are excluded.
            var actualNullable = cols.ToDictionary(c => c.Name, c => c.Nullable, StringComparer.Ordinal);
            foreach (var col in entity.Columns.Where(c => !c.IsPrimaryKey && !c.IsGenerated))
            {
                var present = actualNullable.TryGetValue(col.Name, out var nullable);
                Assert.True(
                    present && nullable == col.IsNullable,
                    $"Nullability mismatch on {entity.TableName}.{col.Name}: model says {(col.IsNullable ? "NULL" : "NOT NULL")}, db reports {(actualNullable.GetValueOrDefault(col.Name) ? "NULL" : "NOT NULL")}."
                );
            }
        }
    }

    [Fact(DisplayName = "Each modelled index is installed with matching uniqueness and key columns")]
    public async Task InstallsTheModelledIndexes()
    {
        foreach (var entity in ActaSchema.Entities)
        {
            var dbIndexes = (await Fixture.ListIndexesAsync(Schema.SchemaName, entity.TableName)).ToDictionary(
                i => i.Name,
                StringComparer.Ordinal
            );
            foreach (var ix in entity.Indexes)
            {
                Assert.True(dbIndexes.TryGetValue(ix.Name, out var db), $"Index {ix.Name} missing from {entity.TableName}.");
                Assert.True(db.IsUnique == ix.IsUnique, $"Index {ix.Name} uniqueness mismatch: model {ix.IsUnique}, db {db.IsUnique}.");
                Assert.True(
                    db.Columns.SequenceEqual(ix.Columns, StringComparer.Ordinal),
                    $"Index {ix.Name} columns mismatch: model [{string.Join(",", ix.Columns)}] db [{string.Join(",", db.Columns)}]."
                );
            }
        }
    }

    [Fact(DisplayName = "Each modelled foreign key is installed with matching target and on-delete action")]
    public async Task InstallsTheModelledForeignKeys()
    {
        foreach (var entity in ActaSchema.Entities)
        {
            if (entity.ForeignKeys.Count == 0)
            {
                continue;
            }
            var dbFks = await Fixture.ListForeignKeysAsync(Schema.SchemaName, entity.TableName);
            foreach (var fk in entity.ForeignKeys)
            {
                var targetTable = ActaSchema.Entities.First(e => e.ClrType == fk.Target).TableName;
                var expectedOnDelete = fk.OnDelete switch
                {
                    DbForeignKeyAction.Cascade => "cascade",
                    DbForeignKeyAction.SetNull => "set_null",
                    _ => "no_action",
                };
                var match = dbFks.FirstOrDefault(d =>
                    d.Column == fk.Column && d.TargetTable == targetTable && d.TargetColumn == fk.TargetColumn
                );
                Assert.True(match.Column == fk.Column, $"FK {entity.TableName}.{fk.Column} -> {targetTable}.{fk.TargetColumn} missing.");
                Assert.True(
                    match.OnDelete == expectedOnDelete,
                    $"FK {entity.TableName}.{fk.Column} on-delete mismatch: model {expectedOnDelete}, db {match.OnDelete}."
                );
            }
        }
    }

    [Fact(DisplayName = "Each modelled check constraint is installed")]
    public async Task InstallsTheModelledChecks()
    {
        foreach (var entity in ActaSchema.Entities)
        {
            if (entity.Checks.Count == 0)
            {
                continue;
            }
            var dbChecks = (await Fixture.ListCheckConstraintsAsync(Schema.SchemaName, entity.TableName))
                .Select(c => c.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var ck in entity.Checks)
            {
                Assert.True(
                    dbChecks.Contains(ck.Name),
                    $"Check {ck.Name} missing from {entity.TableName}. Present: [{string.Join(",", dbChecks)}]"
                );
            }
        }
    }

    [Fact(DisplayName = "No installed column carries an explicit non-default collation")]
    public async Task InstallsNoCollationOverrides()
    {
        // Identifier matching is canonicalized in code (keys fold to lowercase, names are validated
        // lowercase), which only stays provider-stable while columns compare with plain default
        // collation. A CITEXT column or COLLATE clause would silently reintroduce per-provider
        // divergence, so any per-column collation override is drift.
        foreach (var entity in ActaSchema.Entities)
        {
            var overrides = await Fixture.ListCollationOverridesAsync(Schema.SchemaName, entity.TableName);
            Assert.True(overrides.Count == 0, $"Collation overrides on {entity.TableName}: [{string.Join(", ", overrides)}].");
        }
    }
}
