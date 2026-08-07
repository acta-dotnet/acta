using Acta.Emit.Features.Docs;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Emit;

/// <summary>
/// The provision-script emitter with more than one migration on disk, which no real provider has
/// yet: a temp copy of the SQLite provider gains a fabricated M002, and the emitted script must
/// carry both banner pairs in order and still be double-run safe with one history row per
/// migration plus the version-0 stamp row.
/// </summary>
public sealed class ProvisionScriptChainTests : IDisposable
{
    private const string FabricatedM002 = """
        -- M002_add_widgets (sqlite) delta: fabricated for the emitter chain test.
        CREATE TABLE IF NOT EXISTS {{schema}}.widgets (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            gadget_name TEXT NOT NULL
        ) STRICT;

        INSERT INTO {{schema}}.migrations (version, name, installed_schema)
        VALUES (2, 'add_widgets', '{{schema}}')
        ON CONFLICT (version) DO NOTHING;
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
        var providerSource = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta.Sqlite");
        var providerTarget = Path.Combine(_tempRoot, "src", "Acta.Sqlite");
        foreach (var dir in new[] { "Sql", Path.Combine("Schema", "Migrations") })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(providerSource, dir), "*.sql", SearchOption.AllDirectories))
            {
                var target = Path.Combine(providerTarget, Path.GetRelativePath(providerSource, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(providerTarget, "Schema", "Migrations", "M002_add_widgets.sql"), FabricatedM002, ct);

        var script = ProvisionScriptEmitter.Emit(_tempRoot, "sqlite");

        var banners = new[]
        {
            "-- ===== BEGIN M001_init =====",
            "-- ===== END M001_init =====",
            "-- ===== BEGIN M002_add_widgets =====",
            "-- ===== END M002_add_widgets =====",
        };
        var positions = banners.Select(b => script.IndexOf(b, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0, "missing banner"));
        Assert.True(positions.SequenceEqual(positions.OrderBy(p => p)), "banners out of order");

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
            + "(SELECT COUNT(*) FROM main.jobs_view)";
        await using var reader = await probe.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(3, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0, reader.GetInt64(2));
    }
}
