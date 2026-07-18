using Acta.Relational.Connections;
using Acta.SqlServer.Schema;

namespace Acta;

internal sealed class SqlServerProviderBootstrap(SqlProviderOptions options) : IProviderBootstrap
{
    public Task RunAsync(CancellationToken ct) =>
        options.ApplyMigrationsOnStartup
            ? SqlServerSchemaMigrator.EnsureDatabaseAndApplyAsync(options.ConnectionString, options.Schema, ct)
            : Task.CompletedTask;
}
