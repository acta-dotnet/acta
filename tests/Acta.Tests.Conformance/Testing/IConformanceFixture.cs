namespace Acta.Tests.Conformance.Testing;

public readonly record struct DbIndexInfo(string Name, bool IsUnique, IReadOnlyList<string> Columns);

public readonly record struct DbForeignKeyInfo(string Column, string TargetTable, string TargetColumn, string OnDelete);

public readonly record struct DbCheckInfo(string Name);

/// <summary>
/// Per-provider hook bundle injected into conformance spec classes via a generic type argument. The
/// fixture is stateless and constructed via <c>new()</c> by the spec base class.
/// </summary>
/// <remarks>
/// xunit v3 instantiates a fresh test class (and therefore a fresh fixture) per <c>[Fact]</c>, so
/// state on the fixture would leak across parallel-running tests; the instance methods are thin
/// dispatch into static factories that own the schema lifecycle. The generic <c>TFixture</c> parameter
/// keeps the per-provider concrete spec subclasses to one line each.
/// </remarks>
public interface IConformanceFixture
{
    /// <summary>Allocate a fresh isolated schema with M001 applied; throws via <c>Assert.Skip</c> when the provider env var is unset.</summary>
    ValueTask<IIntegrationSchema> CreateSchemaAsync();

    /// <summary>
    /// The user-table names present in <paramref name="schemaName"/>, via a provider catalog query.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListTablesAsync(string schemaName);

    /// <summary>
    /// The user-view names present in <paramref name="schemaName"/>, via a provider catalog query.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListViewsAsync(string schemaName);

    /// <summary>The columns of <paramref name="tableName"/> in <paramref name="schemaName"/> as
    /// (name, nullable) pairs, via a provider catalog query.</summary>
    ValueTask<IReadOnlyList<(string Name, bool Nullable)>> ListColumnsAsync(string schemaName, string tableName);

    /// <summary>Indexes on <paramref name="tableName"/> (by name, with uniqueness flag and ordered key columns).</summary>
    ValueTask<IReadOnlyList<DbIndexInfo>> ListIndexesAsync(string schemaName, string tableName);

    /// <summary>Foreign keys on <paramref name="tableName"/> (one entry per column mapping; OnDelete is canonical lowercase).</summary>
    ValueTask<IReadOnlyList<DbForeignKeyInfo>> ListForeignKeysAsync(string schemaName, string tableName);

    /// <summary>Check constraints on <paramref name="tableName"/> with <c>ck_</c>-prefixed names.</summary>
    ValueTask<IReadOnlyList<DbCheckInfo>> ListCheckConstraintsAsync(string schemaName, string tableName);

    /// <summary>
    /// Columns of <paramref name="tableName"/> whose collation deviates from the database default
    /// (explicit COLLATE clause or a case-insensitive type such as citext). Expected empty: identifier
    /// matching is canonicalized in code, so per-column collation overrides are drift.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListCollationOverridesAsync(string schemaName, string tableName);

    /// <summary>Count user tables in <paramref name="schemaName"/> using a provider-native catalog query.</summary>
    ValueTask<int> CountTablesAsync(string schemaName);

    /// <summary>
    /// Wire the per-provider <c>UseSqlServer</c> / <c>UsePostgres</c> call into
    /// <paramref name="builder"/>, targeting the named <paramref name="schemaName"/>. Used by the
    /// <c>Testing/</c> bases that own DI construction directly (no fixture-built session factory).
    /// </summary>
    void ApplyProvider(Acta.IJobsBuilder builder, string schemaName);
}
