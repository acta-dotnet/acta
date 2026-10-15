using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// The startup every provider's schema migrator runs as its <see cref="IProviderBootstrap"/>. Each provider supplies its
/// driver assembly, the driver major it is certified against, and its two migrator entry points.
/// </summary>
internal abstract class SchemaBootstrap(SqlProviderOptions options, ILogger? log) : IProviderBootstrap
{
    protected abstract Assembly DriverAssembly { get; }

    protected abstract int CertifiedMajor { get; }

    protected abstract Task ApplyOnStartupAsync(string connectionString, string schemaName, CancellationToken ct);

    protected abstract Task PreflightOnStartupAsync(string connectionString, string schemaName, CancellationToken ct);

    public async Task RunAsync(CancellationToken ct)
    {
        // Before any SQL: which driver is in the process, and does this database's history belong to
        // this build. The second check runs whether or not this host applies migrations - the apply
        // path makes the same comparisons, and a host that never applies would otherwise never make
        // them at all.
        DriverVersionPreflight.Run(DriverAssembly, CertifiedMajor, log);

        if (options.ApplyMigrationsOnStartup)
        {
            await ApplyOnStartupAsync(options.ConnectionString, options.Schema, ct);
            return;
        }

        await PreflightOnStartupAsync(options.ConnectionString, options.Schema, ct);
    }
}
