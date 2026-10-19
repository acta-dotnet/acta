using System.Globalization;
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

        log?.LogWarning(
            "Acta: ({Operation}) running on an uncertified database driver ({Detail}); reason ({Reason}).",
            "driver-version-preflight",
            Detail(name.Name ?? driverAssembly.ToString(), loadedMajor, certifiedMajor),
            "driver-major-mismatch"
        );
    }

    internal static string Detail(string driverName, int loadedMajor, int certifiedMajor) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{driverName} major {loadedMajor} is loaded, but this Acta provider package was built and certified against major {certifiedMajor}"
        );
}
