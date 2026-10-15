using Acta.Emit.Features.Docs;
using Acta.Emit.Features.Migrations;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// The provision-script emitter with more than one migration on disk, which no real provider has
/// yet: a temp copy of the SQLite provider gains a fabricated delta, and the emitted script must
/// carry both banner pairs in order and still be double-run safe with one history row per
/// migration.
/// </summary>
public sealed class ProvisionScriptChainTests : IDisposable
{
    private const string Delta = "M99990101000000_add_widgets";

    private const string FabricatedDelta = $"""
        -- {Delta} (sqlite) delta: fabricated for the emitter chain test.
        CREATE TABLE IF NOT EXISTS {"{{schema}}"}.widgets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            gadget_name TEXT NOT NULL
        ) STRICT;

        INSERT INTO {"{{schema}}"}.migrations (name)
        VALUES ('{Delta}')
        ON CONFLICT (name) DO NOTHING;
        """;

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"acta-chain-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task Two_migration_script_carries_ordered_banners_and_double_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        SchemaCommandsTests.CopySql(
            _tempRoot,
            path => path.StartsWith("src/Acta/", StringComparison.Ordinal) || path.StartsWith("src/Acta.Sqlite/", StringComparison.Ordinal)
        );
        var baseline = MigrationFiles.Stems(_tempRoot, "sqlite").Single();
        await File.WriteAllTextAsync(MigrationFiles.PathFor(_tempRoot, "sqlite", Delta), FabricatedDelta, ct);

        var script = ProvisionScriptEmitter.Emit(_tempRoot, "sqlite");

        var banners = new[]
        {
            $"-- ===== BEGIN {baseline} =====",
            $"-- ===== END {baseline} =====",
            $"-- ===== BEGIN {Delta} =====",
            $"-- ===== END {Delta} =====",
        };
        var positions = banners.Select(b => script.IndexOf(b, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0, "missing banner"));
        Assert.True(positions.SequenceEqual(positions.OrderBy(p => p)), "banners out of order");
        Assert.Contains($"WHERE name NOT IN ('{baseline}', '{Delta}')", script, StringComparison.Ordinal);

        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(ct);
        // Twice on purpose, mirroring the published-script specs: install and upgrade are the same
        // file, and the second pass must stamp nothing new.
        for (var pass = 0; pass < 2; pass++)
        {
            await using var provision = conn.CreateCommand();
            provision.CommandText = script;
            await provision.ExecuteNonQueryAsync(ct);
        }

        await using var probe = conn.CreateCommand();
        probe.CommandText =
            "SELECT (SELECT COUNT(*) FROM main.migrations), "
            + "(SELECT COUNT(*) FROM main.widgets), "
            + "(SELECT COUNT(*) FROM main.jobs)";
        await using var reader = await probe.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
    }
}
