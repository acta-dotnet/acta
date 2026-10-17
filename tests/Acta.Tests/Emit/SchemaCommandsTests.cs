using Acta.Emit.Features.Migrations;
using Acta.Emit.Features.Verify;
using Acta.Emit.Shared;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

// These drive the reset/add/check commands against a throwaway repo root so they never touch the
// real provider Migrations trees. SchemaModel.Discover() always reads the live entity assembly. Migrations
// are provider-owned: each lands as a bare M{yyyyMMddHHmmss}_{name}.sql under src/Acta.{Provider}/Migrations.
public sealed class SchemaCommandsTests : IDisposable
{
    private static readonly DateTime First = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Second = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Third = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root;

    public SchemaCommandsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"acta-cmd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // Docs emission resolves each code family by locating src/Acta/<Area>/<Name>.cs by file name
        // only; mirror the real tree as empty files so the throwaway root resolves the same areas.
        var actaRoot = Path.Combine(IntegrationConfig.FindRepoRoot(), "src", "Acta");
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        foreach (var file in Directory.EnumerateFiles(actaRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(actaRoot, file);
            if (relative.Split(separators).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            var target = Path.Combine(_root, "src", "Acta", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Create(target).Dispose();
        }

        // Migration and provision-script emission read the routine and view sources, so the SQL files
        // are mirrored verbatim. The migrations are the commands' own to write.
        CopySql(_root, path => !path.Contains("/Migrations/", StringComparison.Ordinal));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Copies every <c>.sql</c> file under <c>src/</c> that passes the filter to the same relative path under a throwaway root.</summary>
    internal static void CopySql(string root, Func<string, bool> include)
    {
        var source = IntegrationConfig.FindRepoRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "src"), "*.sql", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (
                relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal)
                || !include(relative)
            )
            {
                continue;
            }

            var target = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public void Add_without_a_name_writes_an_init_baseline_per_provider_and_the_snapshot()
    {
        Assert.Equal(0, SchemaAddCommand.Run(name: null, repoRoot: _root, now: First));

        Assert.True(File.Exists(MigrationFiles.PathFor(_root, "pg", "M20260101000000_init")));
        Assert.True(File.Exists(MigrationFiles.PathFor(_root, "mssql", "M20260101000000_init")));
        Assert.True(File.Exists(MigrationFiles.PathFor(_root, "sqlite", "M20260101000000_init")));
        Assert.NotEmpty(SchemaSnapshot.Load(SnapshotFile.Path(_root)).Entities);
    }

    [Fact]
    public void Add_with_a_name_before_the_baseline_is_refused()
    {
        Assert.Equal(2, SchemaAddCommand.Run(name: "first", repoRoot: _root, now: First));
        Assert.Empty(MigrationFiles.Stems(_root, "pg"));
    }

    [Fact]
    public void A_late_joining_provider_gets_a_full_baseline_under_the_runs_stem()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);
        File.Delete(MigrationFiles.PathFor(_root, "sqlite", "M20260101000000_init"));

        Assert.Equal(0, SchemaAddCommand.Run(name: "join_sqlite", repoRoot: _root, now: Second));

        Assert.Equal(["M20260102000000_join_sqlite"], MigrationFiles.Stems(_root, "sqlite"));
        Assert.Contains(
            "CREATE TABLE IF NOT EXISTS {{schema}}.jobs",
            File.ReadAllText(MigrationFiles.PathFor(_root, "sqlite", "M20260102000000_join_sqlite"))
        );
        Assert.Equal(["M20260101000000_init", "M20260102000000_join_sqlite"], MigrationFiles.Stems(_root, "pg"));
    }

    [Fact]
    public void Add_with_no_change_after_the_baseline_is_a_noop()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);
        Assert.Equal(0, SchemaAddCommand.Run(name: "second", repoRoot: _root, now: Second));
        Assert.All(ProviderCatalog.All, p => Assert.Equal(["M20260101000000_init"], MigrationFiles.Stems(_root, p.Token)));
    }

    [Fact]
    public void Add_without_a_name_after_the_baseline_is_refused()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);
        Assert.Equal(2, SchemaAddCommand.Run(name: null, repoRoot: _root, now: Second));
    }

    [Fact]
    public void Add_refuses_a_name_a_migration_already_carries()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);
        File.Delete(MigrationFiles.PathFor(_root, "sqlite", "M20260101000000_init"));
        SchemaAddCommand.Run(name: "join_sqlite", repoRoot: _root, now: Second);

        Assert.Equal(2, SchemaAddCommand.Run(name: "join_sqlite", repoRoot: _root, now: Third));
        Assert.Equal(2, SchemaAddCommand.Run(name: "init", repoRoot: _root, now: Third));
        Assert.Equal(["M20260101000000_init", "M20260102000000_join_sqlite"], MigrationFiles.Stems(_root, "pg"));
    }

    [Fact]
    public void Force_replaces_the_migration_that_carries_the_name_under_the_runs_instant()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);

        Assert.Equal(0, SchemaAddCommand.Run(name: "init", repoRoot: _root, now: Second, force: true));

        Assert.All(ProviderCatalog.All, p => Assert.Equal(["M20260102000000_init"], MigrationFiles.Stems(_root, p.Token)));
    }

    [Fact]
    public void Force_with_nothing_to_rediff_refuses_and_keeps_the_migration()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);
        foreach (var provider in ProviderCatalog.All)
        {
            File.WriteAllText(MigrationFiles.PathFor(_root, provider.Token, "M20260102000000_extra"), "-- extra");
        }

        Assert.Equal(2, SchemaAddCommand.Run(name: "extra", repoRoot: _root, now: Third, force: true));

        Assert.All(
            ProviderCatalog.All,
            p => Assert.Equal(["M20260101000000_init", "M20260102000000_extra"], MigrationFiles.Stems(_root, p.Token))
        );
    }

    [Fact]
    public void Add_rejects_a_non_snake_case_name()
    {
        Assert.Equal(2, SchemaAddCommand.Run(name: "AddX", repoRoot: _root));
    }

    [Fact]
    public void Reset_without_force_keeps_files()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);
        Assert.Equal(2, SchemaResetCommand.Run(force: false, repoRoot: _root));
        Assert.True(File.Exists(MigrationFiles.PathFor(_root, "pg", "M20260101000000_init")));
    }

    [Fact]
    public void Reset_force_deletes_migrations_and_snapshot()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);
        Assert.Equal(0, SchemaResetCommand.Run(force: true, repoRoot: _root));
        Assert.False(File.Exists(MigrationFiles.PathFor(_root, "pg", "M20260101000000_init")));
        Assert.False(File.Exists(SnapshotFile.Path(_root)));
    }

    [Fact]
    public void A_routine_edit_fails_check_until_add_drafts_it_alone_into_the_next_migration()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);
        var source = Path.Combine(_root, "src", "Acta", "Operations", "Execution", "StartStep", "StartStep.routine.pg.sql");
        File.WriteAllText(source, File.ReadAllText(source).Replace("AS $$", "AS $$\n-- edited", StringComparison.Ordinal));

        Assert.Equal(1, CheckCommand.Run(_root));
        Assert.Equal(0, SchemaAddCommand.Run(name: "edit_start_step", repoRoot: _root, now: Second));

        var pg = File.ReadAllText(MigrationFiles.PathFor(_root, "pg", "M20260102000000_edit_start_step"));
        Assert.Contains("CREATE OR REPLACE FUNCTION {{schema}}.start_step(", pg, StringComparison.Ordinal);
        Assert.Contains("-- edited", pg, StringComparison.Ordinal);
        Assert.Contains("VALUES ('M20260102000000_edit_start_step')", pg, StringComparison.Ordinal);
        Assert.DoesNotContain("claim_jobs", pg, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PROCEDURE",
            File.ReadAllText(MigrationFiles.PathFor(_root, "mssql", "M20260102000000_edit_start_step")),
            StringComparison.Ordinal
        );
        Assert.Equal(0, CheckCommand.Run(_root));
    }

    [Fact]
    public void Check_passes_after_add_then_flags_a_snapshot_edited_by_hand()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root, now: First);
        Assert.Equal(0, CheckCommand.Run(_root));

        var path = SnapshotFile.Path(_root);
        var snapshot = SchemaSnapshot.Load(path);
        SchemaSnapshot.Save(snapshot with { Entities = snapshot.Entities.Take(1).ToList() }, path);

        Assert.Equal(1, CheckCommand.Run(_root));
    }
}
