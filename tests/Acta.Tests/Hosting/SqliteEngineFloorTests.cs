using Microsoft.Data.Sqlite;
using Xunit;

namespace Acta.Tests.Hosting;

/// <summary>
/// The SQLite engine floor the provider checks before it applies or preflights a schema. The comparison and the
/// refusal are proven on version strings, so no older native SQLite is needed.
/// </summary>
public sealed class SqliteEngineFloorTests
{
    [Theory]
    [InlineData("3.51.0")]
    [InlineData("3.51.1")]
    [InlineData("3.53.3")]
    [InlineData("3.100.0")]
    [InlineData("4.0.0")]
    public void An_engine_at_or_above_the_floor_is_supported(string engineVersion) =>
        Assert.True(SqliteSchemaMigrator.IsSupportedEngine(engineVersion));

    [Theory]
    [InlineData("3.50.4")]
    [InlineData("3.42.0")]
    // Numeric, not lexical: as text, "3.9.0" sorts after "3.51.0".
    [InlineData("3.9.0")]
    [InlineData("2.8.17")]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void An_engine_below_the_floor_or_with_an_unreadable_version_is_not_supported(string engineVersion) =>
        Assert.False(SqliteSchemaMigrator.IsSupportedEngine(engineVersion));

    [Fact]
    public void The_refusal_names_the_version_found_the_version_required_and_the_bundled_engine()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => SqliteSchemaMigrator.EnsureSupportedEngine("3.50.4"));

        Assert.Contains("'3.50.4'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"SQLite {SqliteSchemaMigrator.MinimumEngineVersion} or newer", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Data.Sqlite bundles", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_engine_Microsoft_Data_Sqlite_bundles_clears_the_floor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);

        var engineVersion = await SqliteSchemaMigrator.ReadEngineVersionAsync(connection, ct);

        Assert.True(SqliteSchemaMigrator.IsSupportedEngine(engineVersion), $"the bundled engine reports {engineVersion}.");
    }
}
