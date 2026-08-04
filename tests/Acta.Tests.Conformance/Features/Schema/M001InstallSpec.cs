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

    // Business probe tables the transactional-enqueue specs create in the shared schema (SQLite has one
    // database for the whole run) are test artifacts, not modelled entities. They all use this prefix.
    private const string BusinessProbePrefix = "acta_txn";

    // External-outbox source tables the outbox specs create per test are producer-owned artifacts, not
    // ledger entities, so they are never part of the modelled Acta schema.
    private const string OutboxProbePrefix = "acta_outbox";

    [Fact(DisplayName = "Schema base tables equal the ActaSchema entity set with nothing missing or extra")]
    public async Task InstallsExactlyTheModelledEntityTables()
    {
        var expected = ActaSchema.Entities.Select(e => e.TableName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var actual = (await Fixture.ListTablesAsync(Schema.SchemaName))
            .Where(t =>
                t != MigrationHistoryTable
                && !t.StartsWith(BusinessProbePrefix, StringComparison.Ordinal)
                && !t.StartsWith(OutboxProbePrefix, StringComparison.Ordinal)
            )
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
        // columns per table: the backstop against a hand-edited delta drifting from ActaSchema.
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

            // 3. Declared width of sized columns. A hand-edited migration that narrows varchar(128) to
            // varchar(64) passes every other assertion here. Only compared where the provider reports a
            // width at all: SQLite drops declared text/blob lengths, and Postgres reports none for bytea.
            var actualWidth = cols.ToDictionary(c => c.Name, c => c.MaxLength, StringComparer.Ordinal);
            foreach (var col in entity.Columns.Where(c => c.Size is not null))
            {
                if (actualWidth.GetValueOrDefault(col.Name) is not { } width)
                {
                    continue;
                }
                Assert.True(
                    width == col.Size,
                    $"Width mismatch on {entity.TableName}.{col.Name}: model says {col.Size}, db reports {width}."
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

    [Fact(DisplayName = "No table carries an index, foreign key or check the model does not declare")]
    public async Task InstallsNoUnmodelledConstraints()
    {
        // Presence assertions alone cannot see a hand-edited migration that ADDS something. An extra
        // index silently changes the query plan on one provider only; an extra constraint silently
        // changes what the database accepts.
        foreach (var entity in ActaSchema.Entities)
        {
            var modelledIndexes = entity.Indexes.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            var extraIndexes = (await Fixture.ListIndexesAsync(Schema.SchemaName, entity.TableName))
                .Select(i => i.Name)
                // Provider-owned backing indexes: the declared PK, and SQLite's implicit UNIQUE indexes.
                .Where(n =>
                    !modelledIndexes.Contains(n)
                    && !n.StartsWith("pk_", StringComparison.Ordinal)
                    && !n.StartsWith("sqlite_autoindex", StringComparison.Ordinal)
                )
                .ToList();
            Assert.True(extraIndexes.Count == 0, $"Unmodelled index on {entity.TableName}: [{string.Join(", ", extraIndexes)}]");

            var modelledFks = entity.ForeignKeys.Select(fk => fk.Column).ToHashSet(StringComparer.Ordinal);
            var extraFks = (await Fixture.ListForeignKeysAsync(Schema.SchemaName, entity.TableName))
                .Where(d => !modelledFks.Contains(d.Column))
                .Select(d => $"{d.Column} -> {d.TargetTable}.{d.TargetColumn}")
                .ToList();
            Assert.True(extraFks.Count == 0, $"Unmodelled foreign key on {entity.TableName}: [{string.Join(", ", extraFks)}]");

            var modelledChecks = entity.Checks.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var extraChecks = (await Fixture.ListCheckConstraintsAsync(Schema.SchemaName, entity.TableName))
                .Select(c => c.Name)
                .Where(n => !modelledChecks.Contains(n) && !IsEmitterSynthesized(n, entity))
                .ToList();
            Assert.True(extraChecks.Count == 0, $"Unmodelled check on {entity.TableName}: [{string.Join(", ", extraChecks)}]");
        }
    }

    /// <summary>
    /// Recognizes the constraints the emitter synthesizes per column rather than from a <c>[DbCheck]</c>:
    /// the closed-family <c>IN</c>-list check, and the Postgres/SQLite byte-range and octet-length checks
    /// that stand in for a native bounded type. Anything else on the table is drift.
    /// </summary>
    private static bool IsEmitterSynthesized(string name, DbEntitySpec entity) =>
        entity.Columns.Any(c =>
            name == $"ck_{entity.TableName}_{c.Name}"
            || name == $"ck_{entity.TableName}_{c.Name}_code"
            || name == $"ck_{entity.TableName}_{c.Name}_byte"
            || name == $"ck_{entity.TableName}_{c.Name}_bytes"
        );

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
