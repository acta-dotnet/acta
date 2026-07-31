using Acta.Runtime.Hosting;
using Acta.Sqlite.Schema;

namespace Acta.Sqlite.Hosting;

internal sealed class SqliteProviderBootstrap(SqlProviderOptions options) : IProviderBootstrap
{
    public Task RunAsync(CancellationToken ct) =>
        options.ApplyMigrationsOnStartup
            ? SqliteSchemaMigrator.EnsureDatabaseAndApplyAsync(options.ConnectionString, options.Schema, ct)
            : Task.CompletedTask;
}
