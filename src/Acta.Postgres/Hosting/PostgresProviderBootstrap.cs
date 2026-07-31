using Acta.Postgres.Schema;
using Acta.Runtime.Hosting;

namespace Acta.Postgres.Hosting;

internal sealed class PostgresProviderBootstrap(SqlProviderOptions options) : IProviderBootstrap
{
    public Task RunAsync(CancellationToken ct) =>
        options.ApplyMigrationsOnStartup
            ? PostgresSchemaMigrator.EnsureDatabaseAndApplyAsync(options.ConnectionString, options.Schema, ct)
            : Task.CompletedTask;
}
