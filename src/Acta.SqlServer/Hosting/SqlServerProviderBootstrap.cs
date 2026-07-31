using Acta.Runtime.Hosting;
using Acta.SqlServer.Schema;

namespace Acta.SqlServer.Hosting;

internal sealed class SqlServerProviderBootstrap(SqlProviderOptions options) : IProviderBootstrap
{
    public Task RunAsync(CancellationToken ct) =>
        options.ApplyMigrationsOnStartup
            ? SqlServerSchemaMigrator.EnsureDatabaseAndApplyAsync(options.ConnectionString, options.Schema, ct)
            : Task.CompletedTask;
}
