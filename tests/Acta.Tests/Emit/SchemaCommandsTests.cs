using Acta.Emit.Features.Migrations;
using Acta.Emit.Features.Verify;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Emit;

// These drive the reset/add/amend/check commands against a throwaway repo root so they never touch the
// real provider Schema/Migrations trees. SchemaModel.Discover() always reads the live entity assembly. Migrations
// are provider-owned: each lands as a bare M{nnn}_{name}.sql under src/Acta.{Provider}/Schema/Migrations.
public sealed class SchemaCommandsTests : IDisposable
{
    private readonly string _root;
    private static readonly string[] sourceArray = new[] { "sqlite", "pg", "mssql" };

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
        CopySql(_root, path => !path.Contains("/Schema/Migrations/", StringComparison.Ordinal));
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

    private static string Project(string suffix) =>
        suffix switch
        {
            "sqlite" => "Acta.Sqlite",
            "pg" => "Acta.Postgres",
            "mssql" => "Acta.SqlServer",
            _ => throw new ArgumentOutOfRangeException(nameof(suffix)),
        };

    private string Mig(string suffix, string bareName) => Path.Combine(_root, "src", Project(suffix), "Schema", "Migrations", bareName);

    private bool AnyMigration(string searchPattern) =>
        sourceArray.Any(s =>
        {
            var dir = Path.Combine(_root, "src", Project(s), "Schema", "Migrations");
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, searchPattern).Any();
        });

    [Fact]
    public void Add_genesis_writes_a_baseline_per_provider_and_a_pair_snapshot()
    {
        Assert.Equal(0, SchemaAddCommand.Run(name: null, repoRoot: _root));

        Assert.True(File.Exists(Mig("pg", "M001_init.sql")));
        Assert.True(File.Exists(Mig("mssql", "M001_init.sql")));
        Assert.True(File.Exists(Mig("sqlite", "M001_init.sql")));

        var pair = SnapshotPair.Load(SnapshotFile.Path(_root));
        Assert.Null(pair.Previous);
        Assert.NotEmpty(pair.Current.Entities);
    }

    [Fact]
    public void Add_with_no_change_after_genesis_is_a_noop()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root);
        Assert.Equal(0, SchemaAddCommand.Run(name: "second", repoRoot: _root));
        Assert.False(AnyMigration("M002_*.sql"));
    }

    [Fact]
    public void Add_rejects_a_non_snake_case_name()
    {
        Assert.Equal(2, SchemaAddCommand.Run(name: "AddX", repoRoot: _root));
    }

    [Fact]
    public void Reset_without_force_keeps_files()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root);
        Assert.Equal(2, SchemaResetCommand.Run(force: false, repoRoot: _root));
        Assert.True(File.Exists(Mig("pg", "M001_init.sql")));
    }

    [Fact]
    public void Reset_force_deletes_migrations_and_snapshot()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root);
        Assert.Equal(0, SchemaResetCommand.Run(force: true, repoRoot: _root));
        Assert.False(File.Exists(Mig("pg", "M001_init.sql")));
        Assert.False(File.Exists(SnapshotFile.Path(_root)));
    }

    [Fact]
    public void Amend_with_no_migrations_errors()
    {
        Assert.Equal(2, SchemaAmendCommand.Run(name: null, repoRoot: _root));
    }

    [Fact]
    public void Amend_with_a_name_renames_the_tip()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root); // M001_init
        Assert.Equal(0, SchemaAmendCommand.Run(name: "genesis", repoRoot: _root));

        Assert.True(File.Exists(Mig("pg", "M001_genesis.sql")));
        Assert.False(File.Exists(Mig("pg", "M001_init.sql")));
    }

    [Fact]
    public void A_routine_edit_fails_check_until_add_drafts_it_alone_into_the_next_migration()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root);
        var source = Path.Combine(_root, "src", "Acta", "Operations", "Execution", "StartStep", "StartStep.pg.sql");
        File.WriteAllText(source, File.ReadAllText(source).Replace("AS $$", "AS $$\n-- edited", StringComparison.Ordinal));

        Assert.Equal(1, CheckCommand.Run(_root));
        Assert.Equal(0, SchemaAddCommand.Run(name: "edit_start_step", repoRoot: _root));

        var pg = File.ReadAllText(Mig("pg", "M002_edit_start_step.sql"));
        Assert.Contains("CREATE OR REPLACE FUNCTION {{schema}}.start_step(", pg, StringComparison.Ordinal);
        Assert.Contains("-- edited", pg, StringComparison.Ordinal);
        Assert.DoesNotContain("claim_jobs", pg, StringComparison.Ordinal);
        Assert.DoesNotContain("PROCEDURE", File.ReadAllText(Mig("mssql", "M002_edit_start_step.sql")), StringComparison.Ordinal);
        Assert.Equal(0, CheckCommand.Run(_root));
    }

    [Fact]
    public void Check_passes_after_add_then_flags_a_mutated_snapshot()
    {
        SchemaAddCommand.Run(name: null, repoRoot: _root);
        Assert.Equal(0, CheckCommand.Run(_root));

        var path = SnapshotFile.Path(_root);
        var pair = SnapshotPair.Load(path);
        var trimmed = pair.Current with { Entities = pair.Current.Entities.Take(1).ToList() };
        SnapshotPair.Save(new SnapshotPair(trimmed, pair.Previous), path);

        Assert.Equal(1, CheckCommand.Run(_root));
    }
}
