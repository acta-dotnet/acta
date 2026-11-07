using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// The startup every provider's schema migrator runs as its <see cref="IProviderBootstrap"/>. Each provider supplies its
/// driver assembly, the driver major it is certified against, and its three migrator entry points.
/// </summary>
internal abstract class SchemaBootstrap(SqlProviderOptions options, ILogger? log) : IProviderBootstrap
{
    protected abstract Assembly DriverAssembly { get; }

    protected abstract int CertifiedMajor { get; }

    /// <summary>The deploy step's apply: a server provider's database must exist, and SQLite's open creates the file.</summary>
    protected abstract Task<IReadOnlyList<string>> ApplyOnDeployAsync(string connectionString, string schemaName, CancellationToken ct);

    /// <summary>
    /// The development apply. A server provider creates the database when it is missing first; SQLite's open creates the
    /// file, so its deploy apply serves both.
    /// </summary>
    protected virtual Task<IReadOnlyList<string>> ApplyOnStartupAsync(string connectionString, string schemaName, CancellationToken ct) =>
        ApplyOnDeployAsync(connectionString, schemaName, ct);

    protected abstract Task PreflightOnStartupAsync(string connectionString, string schemaName, CancellationToken ct);

    public async Task RunAsync(CancellationToken ct)
    {
        // Before any SQL: which driver is in the process, and does this database's history belong to
        // this build. The second check runs whether or not this host applies migrations - the apply
        // path makes the same comparisons, and a host that never applies would otherwise never make
        // them at all.
        DriverVersionPreflight.Run(DriverAssembly, CertifiedMajor, log);

        if (options.MigrateOnStartup)
        {
            var applied = await ApplyOnStartupAsync(options.ConnectionString, options.Schema, ct);
            if (applied.Count > 0 && log?.IsEnabled(LogLevel.Information) == true)
            {
                var detail = $"schema {options.Schema}: {string.Join(", ", applied)}";
                ActaLog.MigrationsApplied(log, detail);
            }
            return;
        }

        await PreflightOnStartupAsync(options.ConnectionString, options.Schema, ct);
    }

    /// <summary>
    /// What <c>jobs migrate</c> runs: the deploy apply with the configured connection string and schema. Returns the schema
    /// and the names applied, in order.
    /// </summary>
    public async Task<(string Schema, IReadOnlyList<string> Applied)> MigrateAsync(CancellationToken ct) =>
        (options.Schema, await ApplyOnDeployAsync(options.ConnectionString, options.Schema, ct));
}

/// <summary>
/// Compares the ADO driver assembly actually loaded into the process against the major version the provider package
/// was built and certified against, at provider bootstrap, and warns once on a mismatch. The package dependency is an
/// unbounded floor, so the host decides the driver version; this makes an uncertified major visible without stopping a
/// host whose driver moved because another library pulled the next major in.
/// </summary>
internal static class DriverVersionPreflight
{
    // Each provider package owns its certified major as a constant next to its package reference; this class owns the
    // comparison and the message, so all three providers say the same thing in the same words. A mismatch in either
    // direction is reported: a driver behind the certified major can be missing behavior Acta relies on, and a driver
    // ahead of it is a new major because its authors reserved the right to change something.

    /// <summary>Logs one warning when <paramref name="driverAssembly"/>'s major version is not <paramref name="certifiedMajor"/>.</summary>
    public static void Run(Assembly driverAssembly, int certifiedMajor, ILogger? log)
    {
        var name = driverAssembly.GetName();
        var loadedMajor = name.Version?.Major ?? 0;
        if (loadedMajor == certifiedMajor)
        {
            return;
        }

        if (log?.IsEnabled(LogLevel.Warning) == true)
        {
            ActaLog.UncertifiedDriver(log, Detail(name.Name ?? driverAssembly.ToString(), loadedMajor, certifiedMajor));
        }
    }

    internal static string Detail(string driverName, int loadedMajor, int certifiedMajor) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{driverName} major {loadedMajor} is loaded, but this Acta provider package was built and certified against major {certifiedMajor}"
        );
}
