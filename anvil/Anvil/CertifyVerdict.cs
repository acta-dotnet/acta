using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Acta;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Anvil;

/// <summary>
/// Runs <c>certify.sql</c> against a finished run and prints one verdict.
/// </summary>
/// <remarks>
/// The point is that the operator does not have to interpret anything. Running the checks by hand
/// means knowing which ones are inverted, which need a quiesced run, and that nothing interesting can
/// happen before the lease floor has passed - every one of which produced a wrong reading during the
/// first runs. The file stays the single source of truth; this only executes it.
/// </remarks>
internal static class CertifyVerdict
{
    // Measured checks report a number and never fail: they describe the run rather than judge it.
    private sealed record Check(string Name, string Sql, bool Inverted, bool Measured);

    public static async Task<int> RunAsync(string provider, string schema, CancellationToken ct)
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var connectionString = LocalDatabase.ResolveConnectionString(configuration, provider, schema);
        var prefix = LocalDatabase.IsSqlite(provider) ? "" : schema + ".";

        await using var connection = Create(provider, connectionString);
        await connection.OpenAsync(ct);

        var checks = Parse(LoadSql().Replace("{s}", prefix, StringComparison.Ordinal));
        var failures = new List<string>();
        var measured = new List<string>();

        Console.WriteLine();
        Console.WriteLine($"  ACTA CERTIFICATION  |  {provider}  |  {schema}");
        Console.WriteLine();

        foreach (var check in checks)
        {
            var (rows, first) = await CountAsync(connection, check.Sql, ct);
            var ok = check.Measured || (check.Inverted ? rows > 0 : rows == 0);
            if (!ok)
            {
                failures.Add(check.Name);
            }
            if (check.Inverted || check.Measured)
            {
                measured.Add(first ?? "(no row)");
            }

            var verdict =
                check.Measured ? "note"
                : ok ? "ok  "
                : "FAIL";
            var detail =
                check.Measured ? first ?? "(nothing recorded)"
                : check.Inverted ? first ?? "no reclaims observed"
                : rows == 0 ? "0"
                : $"{rows} row(s)";
            Console.WriteLine($"  [{verdict}] {check.Name, -28} {detail}");
        }

        if (!await RateContractAsync(connection, prefix, ct))
        {
            failures.Add("rate-contract");
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("  PASS - every asserted property held and the chaos was real.");
            Console.WriteLine();
            return 0;
        }

        // A run whose only failure is the chaos guard is not a failed system; it is a run that was too
        // short to prove anything, which is a different thing and must not read as FAIL.
        if (failures is ["chaos-was-real"])
        {
            Console.WriteLine("  INCONCLUSIVE - no reclaims were observed, so the run proves nothing.");
            Console.WriteLine("  Reclaim needs LeaseTtlSeconds (180s) plus a recovery tick: run for longer,");
            Console.WriteLine("  and size the run by duration rather than by job count.");
            Console.WriteLine();
            return 2;
        }

        Console.WriteLine($"  FAIL - {string.Join(", ", failures)}");
        Console.WriteLine("  If the run has not quiesced, checks marked QUIESCED ONLY report false failures:");
        Console.WriteLine("  wait until no runtimes row is Dispatched or Executing, then re-run.");
        Console.WriteLine();
        return 1;
    }

    // How many over-budget windows are printed by name before the rest are summed up. A meter that
    // over-admits does it in a run of adjacent windows, so the first few carry the finding and the
    // hundreds behind them would only bury the checks above.
    private const int ReportedWindows = 10;

    /// <summary>
    /// Check 14, the rate contract, computed here rather than in <c>certify.sql</c>: counting a sliding
    /// window needs timestamp arithmetic with a different spelling on each provider that file runs on.
    /// Prints one line per over-budget window and always a note line carrying the numbers, and returns
    /// whether the meter held.
    /// </summary>
    private static async Task<bool> RateContractAsync(DbConnection connection, string prefix, CancellationToken ct)
    {
        var (perSecond, burst, validitySeconds) = Meter(MeteredJob.Rate);
        var instants = await AdmissionsAsync(connection, prefix, ct);
        var budget1 = Budget(perSecond, burst, validitySeconds, 1);
        var budget10 = Budget(perSecond, burst, validitySeconds, 10);
        var (max1, over1) = Scan(instants, 1, budget1);
        var (max10, over10) = Scan(instants, 10, budget10);
        var violations = over1.Concat(over10).ToList();

        foreach (var window in violations.Take(ReportedWindows))
        {
            Console.WriteLine($"  [FAIL] {"rate-contract", -28} {window}");
        }
        if (violations.Count > ReportedWindows)
        {
            Console.WriteLine($"  [FAIL] {"rate-contract", -28} and {violations.Count - ReportedWindows} further window(s) over budget");
        }

        // Printed on a passing run too: the seal's claim is the measured envelope, not the absence of
        // a failure line, and a reader cannot judge the margin without the busiest window beside it.
        Console.WriteLine(
            $"  [note] {"rate-contract", -28} rate={MeteredJob.Rate} admitted={instants.Count}"
                + $" max_1s={max1} budget_1s={budget1} max_10s={max10} budget_10s={budget10}"
        );
        return violations.Count == 0;
    }

    // The two numbers the meter is built from, derived from the declared rate exactly as RateLimitSpec
    // derives them: the emission interval is the period divided by the count and rounded up, and the
    // burst is one second's worth of the rate, floored, never below one. Also the validity: a booked
    // turn stays good for a second past its instant, or for one interval when that is longer, which is
    // how many extra admissions a window may hold on top of its own share.
    private static (double PerSecond, int Burst, double ValiditySeconds) Meter(string rate)
    {
        var count = int.Parse(rate[..rate.IndexOf('/', StringComparison.Ordinal)], CultureInfo.InvariantCulture);
        var periodMilliseconds = rate[^1] switch
        {
            's' => 1_000,
            'm' => 60_000,
            _ => 3_600_000,
        };
        var intervalMilliseconds = (periodMilliseconds + count - 1) / count;
        return (
            1_000.0 / intervalMilliseconds,
            (int)Math.Max(1, count * 1_000L / periodMilliseconds),
            Math.Max(1.0, intervalMilliseconds / 1_000.0)
        );
    }

    // The contract in one line: R*(T + W) + B admitted starts in any window of T seconds, floored,
    // because a budget is a count and a fractional turn cannot be taken.
    private static int Budget(double perSecond, int burst, double validitySeconds, double seconds) =>
        (int)Math.Floor((perSecond * (seconds + validitySeconds)) + burst);

    // Every admission opens a window of its own, so scanning [t, t + T) from each one covers every
    // window that can hold a maximum. The end pointer never walks backwards as the start advances,
    // which is what keeps this linear over a run's worth of notes.
    private static (int Max, List<string> Violations) Scan(IReadOnlyList<DateTime> instants, double seconds, int budget)
    {
        var window = TimeSpan.FromSeconds(seconds);
        var violations = new List<string>();
        var max = 0;
        var end = 0;
        for (var start = 0; start < instants.Count; start++)
        {
            end = Math.Max(end, start);
            while (end < instants.Count && instants[end] - instants[start] < window)
            {
                end++;
            }

            var admitted = end - start;
            max = Math.Max(max, admitted);
            if (admitted > budget)
            {
                var from = instants[start].ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                violations.Add($"{seconds:0}s window from {from} admitted {admitted}, budget {budget}");
            }
        }
        return (max, violations);
    }

    // The witness: one note per admitted attempt, written by the metered body before it does anything
    // else. Joined through definitions rather than trusted to the note text alone, so another shape
    // writing the same words could not be read as an admission.
    private static async Task<IReadOnlyList<DateTime>> AdmissionsAsync(DbConnection connection, string prefix, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.created_at_utc
            FROM   {prefix}events e
            JOIN   {prefix}jobs j ON j.id = e.job_id
            JOIN   {prefix}definitions d ON d.id = j.definition_id
            WHERE  e.event_code = 90
              AND  e.reason_message = 'metered-admitted'
              AND  d.name = 'metered'
            ORDER  BY e.created_at_utc
            """;
        var instants = new List<DateTime>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // SQLite stores an instant as epoch milliseconds; the server providers hand back a DateTime.
            var value = reader.GetValue(0);
            instants.Add(
                value is long ms ? DateTime.UnixEpoch.AddMilliseconds(ms) : Convert.ToDateTime(value, CultureInfo.InvariantCulture)
            );
        }
        return instants;
    }

    private static async Task<(int Rows, string? First)> CountAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = 0;
        string? first = null;
        while (await reader.ReadAsync(ct))
        {
            if (rows++ == 0)
            {
                var parts = new List<string>();
                for (var i = 1; i < reader.FieldCount; i++)
                {
                    parts.Add($"{reader.GetName(i)}={(reader.IsDBNull(i) ? "null" : reader.GetValue(i))}");
                }
                first = string.Join(" ", parts);
            }
        }
        return (rows, first);
    }

    // Statements are separated by a bare `;` at end of line; each carries its own name as the first
    // projected literal, and the preceding comment block flags an inverted check.
    private static IReadOnlyList<Check> Parse(string file)
    {
        var checks = new List<Check>();
        foreach (var chunk in file.Split(";\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var statement = string.Join(
                '\n',
                chunk.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal))
            );
            if (string.IsNullOrWhiteSpace(statement))
            {
                continue;
            }

            var open = statement.IndexOf('\'');
            var close = open < 0 ? -1 : statement.IndexOf('\'', open + 1);
            if (open < 0 || close < 0)
            {
                continue;
            }

            checks.Add(
                new Check(
                    statement[(open + 1)..close],
                    statement,
                    chunk.Contains("[INVERTED", StringComparison.Ordinal),
                    chunk.Contains("[MEASURED", StringComparison.Ordinal)
                )
            );
        }
        return checks;
    }

    private static string LoadSql()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("certify.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    private static DbConnection Create(string provider, string connectionString) =>
        LocalDatabase.IsSqlite(provider) ? new SqliteConnection(connectionString)
        : LocalDatabase.IsSqlServer(provider) ? new SqlConnection(connectionString)
        : new NpgsqlConnection(connectionString);
}
