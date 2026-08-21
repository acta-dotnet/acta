using System.Reflection;
using System.Text.RegularExpressions;
using Acta.Relational.Connections;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Hosting;

/// <summary>
/// The driver-major preflight is the only real lock on which ADO driver a host loads, because the
/// package dependency is an unbounded floor by policy. These cover the comparison, what the message
/// has to name for an operator to act on it, and the one escape hatch.
/// </summary>
public sealed class DriverVersionPreflightTests
{
    private static readonly Assembly Sqlite = typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly;

    private static int LoadedMajor(Assembly assembly) => assembly.GetName().Version!.Major;

    [Fact]
    public void Matching_major_passes_under_either_policy()
    {
        var major = LoadedMajor(Sqlite);
        DriverVersionPreflight.Run(Sqlite, major, DriverVersionPolicy.Fail, log: null);
        DriverVersionPreflight.Run(Sqlite, major, DriverVersionPolicy.Warn, log: null);
    }

    [Fact]
    public void Newer_loaded_major_fails_by_default()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DriverVersionPreflight.Run(Sqlite, LoadedMajor(Sqlite) - 1, DriverVersionPolicy.Fail, log: null)
        );
        Assert.Contains("Microsoft.Data.Sqlite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Older_loaded_major_fails_too()
    {
        // Both directions: a driver behind the certified major can be missing behavior Acta relies on.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DriverVersionPreflight.Run(Sqlite, LoadedMajor(Sqlite) + 1, DriverVersionPolicy.Fail, log: null)
        );
        Assert.Contains("Microsoft.Data.Sqlite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_message_names_both_majors_and_the_escape_hatch()
    {
        var loaded = LoadedMajor(Sqlite);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DriverVersionPreflight.Run(Sqlite, loaded + 3, DriverVersionPolicy.Fail, log: null)
        );

        Assert.Contains($"major {loaded} is loaded", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"certified against major {loaded + 3}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DriverVersionPolicy.Warn", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Warn_logs_exactly_one_structured_warning_and_continues()
    {
        var loaded = LoadedMajor(Sqlite);
        var log = new CapturingLogger();

        DriverVersionPreflight.Run(Sqlite, loaded + 3, DriverVersionPolicy.Warn, log);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("driver-version-preflight", entry.Values["Operation"]);
        Assert.Equal("driver-major-mismatch", entry.Values["Reason"]);
        Assert.Contains($"major {loaded} is loaded", (string)entry.Values["Detail"]!, StringComparison.Ordinal);
        Assert.Contains($"major {loaded + 3}", (string)entry.Values["Detail"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Warn_on_a_matching_major_says_nothing()
    {
        var log = new CapturingLogger();
        DriverVersionPreflight.Run(Sqlite, LoadedMajor(Sqlite), DriverVersionPolicy.Warn, log);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Fail_is_the_default_policy() => Assert.Equal(DriverVersionPolicy.Fail, default(DriverVersionPolicy));

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
    [InlineData("Acta.Sqlite", "Acta.Sqlite.Hosting.SqliteProviderBootstrap", "Microsoft.Data.Sqlite")]
    [InlineData("Acta.Postgres", "Acta.Postgres.Hosting.PostgresProviderBootstrap", "Npgsql")]
    [InlineData("Acta.SqlServer", "Acta.SqlServer.Hosting.SqlServerProviderBootstrap", "Microsoft.Data.SqlClient")]
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
        var match = PackageVersionRegex(package).Match(props);
        Assert.True(match.Success, $"Directory.Packages.props declares no PackageVersion for {package}.");

        Assert.Equal(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), certified);
    }

    [Theory]
    [InlineData("Acta.Sqlite", "Acta.Sqlite.Hosting.SqliteProviderBootstrap", "Microsoft.Data.Sqlite")]
    [InlineData("Acta.Postgres", "Acta.Postgres.Hosting.PostgresProviderBootstrap", "Npgsql")]
    [InlineData("Acta.SqlServer", "Acta.SqlServer.Hosting.SqlServerProviderBootstrap", "Microsoft.Data.SqlClient")]
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

    private static Regex PackageVersionRegex(string package) =>
        new(
            $"""<PackageVersion Include="{Regex.Escape(package)}" Version="(\d+)\.""",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5)
        );
}
