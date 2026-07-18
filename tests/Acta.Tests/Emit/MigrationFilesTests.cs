using Acta.Emit.Features.Migrations;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class MigrationFilesTests : IDisposable
{
    private readonly string _root;

    public MigrationFilesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"acta-mf-{Guid.NewGuid():N}");
        // Migrations are provider-owned: one Schema/Migrations folder per provider package, bare M{nnn}_{name}.sql.
        foreach (var project in new[] { "Acta.Sqlite", "Acta.Postgres", "Acta.SqlServer" })
        {
            Directory.CreateDirectory(Path.Combine(_root, "src", project, "Schema", "Migrations"));
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Project(string suffix) =>
        suffix switch
        {
            "sqlite" => "Acta.Sqlite",
            "pg" => "Acta.Postgres",
            "mssql" => "Acta.SqlServer",
            _ => throw new ArgumentOutOfRangeException(nameof(suffix)),
        };

    // Writes a bare migration file into the suffix's provider package.
    private void Touch(string suffix, string fileName) =>
        File.WriteAllText(Path.Combine(_root, "src", Project(suffix), "Schema", "Migrations", fileName), "-- x");

    [Fact]
    public void Empty_dir_has_version_zero_and_next_one()
    {
        Assert.Equal(0, MigrationFiles.CurrentMaxVersion(_root));
        Assert.Equal(1, MigrationFiles.NextVersion(_root));
    }

    [Fact]
    public void Max_version_is_global_across_providers_with_holes()
    {
        Touch("pg", "M001_init.sql");
        Touch("mssql", "M001_init.sql");
        Touch("pg", "M002_add_x.sql"); // mssql has a hole at 2
        Touch("sqlite", "M011_init.sql"); // late provider, leading hole

        Assert.Equal(11, MigrationFiles.CurrentMaxVersion(_root));
        Assert.Equal(12, MigrationFiles.NextVersion(_root));
    }

    [Fact]
    public void ProviderHasFilesBelow_distinguishes_delta_from_baseline()
    {
        Touch("pg", "M001_init.sql");
        Assert.True(MigrationFiles.ProviderHasFilesBelow(_root, "pg", 2)); // pg has M001 < 2 → delta
        Assert.False(MigrationFiles.ProviderHasFilesBelow(_root, "sqlite", 2)); // sqlite none → baseline
    }

    [Fact]
    public void TipName_reads_name_back_from_filename()
    {
        Touch("pg", "M002_add_tenant.sql");
        Assert.Equal("add_tenant", MigrationFiles.TipName(_root, "pg", 2));
        Assert.Null(MigrationFiles.TipName(_root, "mssql", 2));
    }

    [Theory]
    [InlineData("add_tenant", true)]
    [InlineData("init", true)]
    [InlineData("AddTenant", false)]
    [InlineData("add-tenant", false)]
    [InlineData("2add", false)]
    public void IsValidName_enforces_snake_case(string name, bool valid) => Assert.Equal(valid, MigrationFiles.IsValidName(name));

    [Fact]
    public void DefaultName_is_init_for_genesis_else_change()
    {
        Assert.Equal("init", MigrationFiles.DefaultName(isGenesis: true));
        Assert.Equal("change", MigrationFiles.DefaultName(isGenesis: false));
    }
}
