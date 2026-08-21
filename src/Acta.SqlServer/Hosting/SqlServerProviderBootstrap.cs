using Acta.Relational.Connections;
using Acta.Runtime.Hosting;
using Acta.SqlServer.Schema;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Acta.SqlServer.Hosting;

internal sealed class SqlServerProviderBootstrap(SqlProviderOptions options, ILogger<SqlServerProviderBootstrap>? log = null)
    : IProviderBootstrap
{
    /// <summary>
    /// The Microsoft.Data.SqlClient major this package is built and certified against. Bound to the
    /// <c>Microsoft.Data.SqlClient</c> version pinned in <c>Directory.Packages.props</c>:
    /// <c>DriverMajorParityTests</c> fails the build if the two ever say different things, so bump
    /// them together and only after the suite has run against the new major.
    /// </summary>
    internal const int CertifiedDriverMajor = 7;

    public async Task RunAsync(CancellationToken ct)
    {
        // Before any SQL: which driver is in the process, and does this database's history belong to
        // this build. The second check runs whether or not this host applies migrations - the apply
        // path makes the same comparisons, and a host that never applies would otherwise never make
        // them at all.
        DriverVersionPreflight.Run(typeof(SqlConnection).Assembly, CertifiedDriverMajor, options.DriverVersionPolicy, log);

        if (options.ApplyMigrationsOnStartup)
        {
            await SqlServerSchemaMigrator.EnsureDatabaseAndApplyAsync(options.ConnectionString, options.Schema, ct);
            return;
        }

        await SqlServerSchemaMigrator.PreflightAsync(options.ConnectionString, options.Schema, ct);
    }
}
