using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Hosting;

/// <summary>
/// The driver-major check at provider bootstrap: the package dependency is an unbounded floor, so the host decides
/// the driver version, and a major Acta was not certified on is reported once and never stops the host.
/// </summary>
public sealed class DriverVersionPreflightTests
{
    private static readonly Assembly Sqlite = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly;

    private static int LoadedMajor(Assembly assembly) => assembly.GetName().Version!.Major;

    [Fact]
    public void A_matching_major_says_nothing()
    {
        var log = new CapturingLogger();
        DriverVersionPreflight.Run(Sqlite, LoadedMajor(Sqlite), log);
        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    public void A_different_major_logs_one_warning_naming_both_and_continues(int offset)
    {
        // Both directions: a driver behind the certified major can be missing behavior Acta relies on, and one ahead
        // of it is a new major because its authors reserved the right to change something.
        var loaded = LoadedMajor(Sqlite);
        var log = new CapturingLogger();

        DriverVersionPreflight.Run(Sqlite, loaded - offset, log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("driver-version-preflight", entry.Values["Operation"]);
        Assert.Equal("driver-major-mismatch", entry.Values["Reason"]);
        Assert.Contains($"Microsoft.Data.Sqlite major {loaded} is loaded", (string)entry.Values["Detail"]!, StringComparison.Ordinal);
        Assert.Contains($"certified against major {loaded - offset}", (string)entry.Values["Detail"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_without_logging_still_starts() => DriverVersionPreflight.Run(Sqlite, LoadedMajor(Sqlite) + 3, log: null);

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, IReadOnlyDictionary<string, object?> Values)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            Entries.Add(
                (
                    logLevel,
                    (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                        ?? new Dictionary<string, object?>(StringComparer.Ordinal)
                )
            );
    }
}

/// <summary>
/// Each provider package hard-codes the driver major it was certified against, next to a comment that
/// binds it to the version pinned in Directory.Packages.props. A comment cannot fail a build, so the
/// binding is compared here: bumping the package without re-certifying, or re-certifying without
/// bumping the package, stops being silent.
/// </summary>
public sealed partial class DriverMajorParityTests
{
    [Theory]
    [InlineData("Acta.Sqlite", "Acta.Sqlite.SqliteProviderBootstrap", "Microsoft.Data.Sqlite")]
    [InlineData("Acta.Postgres", "Acta.Postgres.PostgresProviderBootstrap", "Npgsql")]
    [InlineData("Acta.SqlServer", "Acta.SqlServer.SqlServerProviderBootstrap", "Microsoft.Data.SqlClient")]
    public void Certified_major_matches_the_centrally_pinned_package(string assemblyName, string bootstrapType, string package)
    {
        var certified =
            Assembly
                .Load(assemblyName)
                .GetType(bootstrapType, throwOnError: true)!
                .GetField("CertifiedDriverMajor", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetRawConstantValue() as int?;
        Assert.True(certified is not null, $"{bootstrapType}.CertifiedDriverMajor was not found; the parity guard is silently passing.");

        var props = File.ReadAllText(Path.Combine(IntegrationConfig.FindRepoRoot(), "Directory.Packages.props"));
        var pinned = PackageVersionRegex()
            .Matches(props)
            .FirstOrDefault(m => string.Equals(m.Groups[1].Value, package, StringComparison.Ordinal));
        Assert.True(pinned is not null, $"Directory.Packages.props declares no PackageVersion for {package}.");

        Assert.Equal(int.Parse(pinned!.Groups[2].Value, CultureInfo.InvariantCulture), certified);
    }

    [Theory]
    [InlineData("Acta.Sqlite", "Acta.Sqlite.SqliteProviderBootstrap", "Microsoft.Data.Sqlite")]
    [InlineData("Acta.Postgres", "Acta.Postgres.PostgresProviderBootstrap", "Npgsql")]
    [InlineData("Acta.SqlServer", "Acta.SqlServer.SqlServerProviderBootstrap", "Microsoft.Data.SqlClient")]
    public void Certified_major_matches_the_driver_the_tests_actually_load(string assemblyName, string bootstrapType, string package)
    {
        // The suite runs against the restored driver, so the constant is only meaningful if what runs
        // here is what it claims to certify.
        var certified = (int)
            Assembly
                .Load(assemblyName)
                .GetType(bootstrapType, throwOnError: true)!
                .GetField("CertifiedDriverMajor", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetRawConstantValue()!;

        Assert.Equal(Assembly.Load(package).GetName().Version!.Major, certified);
    }

    // Every central pin, captured as (id, major); the caller picks the package it is checking. One
    // pattern rather than one built per package, so it can be source-generated like the rest.
    [GeneratedRegex("""<PackageVersion Include="([^"]+)" Version="(\d+)\.""", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersionRegex();
}
