using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using Acta.Tests.Conformance.Sql;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// A spec class's own schema beside the shared one in the test database, migrated by this build on first touch in a
/// process and kept like the shared schema; dropping the test database drops it too. Its name carries the class and the
/// stamp of the provider's baseline migration, so a re-cut baseline gets a fresh schema rather than meeting the old
/// one's history. The facts of a class run one at a time, so no other spec's statement ever meets theirs in it.
/// </summary>
internal sealed class ClassSchema(string schemaName, string connectionString) : IIntegrationSchema
{
    private static readonly ConcurrentDictionary<string, Task> s_migrations = new(StringComparer.Ordinal);

    public string SchemaName => schemaName;

    public string ConnectionString => connectionString;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The datetime stamp of the provider's baseline migration, <c>20261106111104</c> of <c>M20261106111104_init</c>.</summary>
    private static string BaselineStamp(string dialectToken) =>
        SchemaMigrations.Discover(Assembly.Load(ProviderSqlResources.ProviderAssemblyName(dialectToken)))[0].Name[1..15];

    /// <summary>
    /// The schema of <paramref name="spec"/>, beside <paramref name="shared"/>. On SQLite it is the shared file itself:
    /// one writer forms no deadlock, and a schema there is a database file of its own.
    /// </summary>
    public static async ValueTask<IIntegrationSchema> EnsureReadyAsync(IConformanceFixture fixture, IIntegrationSchema shared, Type spec)
    {
        if (fixture.DialectToken == "sqlite")
        {
            return shared;
        }

        var name = $"{IntegrationConfig.TestSchemaName}_{spec.Name.ToLowerInvariant()}_{BaselineStamp(fixture.DialectToken)}";
        // A PostgreSQL handle also names its schema on the search path.
        var builder = new DbConnectionStringBuilder { ConnectionString = shared.ConnectionString };
        if (builder.ContainsKey("Search Path"))
        {
            builder["Search Path"] = name;
        }

        var migration = s_migrations.GetOrAdd(name, n => fixture.ApplyMigrationsAsync(builder.ConnectionString, n, CancellationToken.None));
        try
        {
            await migration;
        }
        catch
        {
            // Only a migration that succeeded is kept, so the class's next fact tries again.
            s_migrations.TryRemove(KeyValuePair.Create(name, migration));
            throw;
        }

        return new ClassSchema(name, builder.ConnectionString);
    }
}
