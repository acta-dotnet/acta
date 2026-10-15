using Acta.Emit.Features.Migrations;
using Xunit;

namespace Acta.Tests.Emit;

public sealed class MigrationFilesTests : IDisposable
{
    private readonly string _root;

    public MigrationFilesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"acta-mf-{Guid.NewGuid():N}");
        // Migrations are provider-owned: one Schema/Migrations folder per provider package, bare M{yyyyMMddHHmmss}_{name}.sql.
        foreach (var project in new[] { "Acta.Sqlite", "Acta.Postgres", "Acta.SqlServer" })
        {
            Directory.CreateDirectory(Path.Combine(_root, "src", project, "Schema", "Migrations"));
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Touch(string token, string stem) => File.WriteAllText(MigrationFiles.PathFor(_root, token, stem), "-- x");

    [Fact]
    public void Stems_are_per_provider_in_ordinal_order()
    {
        Touch("pg", "M20260201000000_add_x");
        Touch("pg", "M20260101000000_init");
        Touch("sqlite", "M20260301000000_init"); // late provider

        Assert.Equal(["M20260101000000_init", "M20260201000000_add_x"], MigrationFiles.Stems(_root, "pg"));
        Assert.Equal(["M20260301000000_init"], MigrationFiles.Stems(_root, "sqlite"));
        Assert.Empty(MigrationFiles.Stems(_root, "mssql"));
    }

    [Theory]
    [InlineData("add_tenant", true)]
    [InlineData("init", true)]
    [InlineData("AddTenant", false)]
    [InlineData("add-tenant", false)]
    [InlineData("2add", false)]
    public void IsValidName_enforces_snake_case(string name, bool valid) => Assert.Equal(valid, MigrationFiles.IsValidName(name));
}
