using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Acta.Bench;

/// <summary>
/// Runs <c>certify.sql</c> and the rate contract against each trial a case result came from, records every check in
/// that trial's evidence, and folds a violation into the result as a safety failure.
/// </summary>
internal static partial class LedgerValidation
{
    /// <summary>How many of a check's offending rows are recorded by id; the count still covers every row.</summary>
    private const int RecordedRows = 100;

    private enum CheckKind
    {
        AnyTime,
        Quiesced,
        Inverted,
        Measured,
    }

    private sealed record LedgerQuery(string Name, CheckKind Kind, string Sql);

    private sealed record TrialLedger(Check[] Checks, IReadOnlyList<string> Violated, bool FaultLanded, Dictionary<string, double> Counts);

    private static readonly LedgerQuery[] Queries = Parse(ReadCertifySql());

    public static async Task<CaseResult> ApplyAsync(
        CaseResult result,
        IReadOnlyList<OwnedProvider> trials,
        EvidenceBundle bundle,
        CancellationToken ct
    )
    {
        if (trials.Count == 0)
        {
            return result;
        }
        var quiesced = result.Liveness == "PASS";
        var faultMustLand = result.Phase == "fault" && FaultCaseRunner.RequiresReclaim(result.Id);
        var metrics = new Dictionary<string, Metric>(result.Metrics, StringComparer.Ordinal);
        List<string> evidence = [.. result.Evidence];
        List<string> violated = [];
        var landed = false;
        string? unavailable = null;
        foreach (var trial in trials)
        {
            TrialLedger ledger;
            try
            {
                ledger = await RunAsync(trial, quiesced, faultMustLand, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unavailable = $"Ledger validation could not read trial {trial.Name} ({ex.GetType().Name}: {ex.Message}).";
                Console.WriteLine($"  ledger {trial.Name}: {unavailable}");
                continue;
            }
            var path = $"histories/{trial.Name}-ledger.json";
            bundle.Write(path, ledger.Checks, BenchJson.Default.CheckArray);
            evidence.Add(path);
            violated.AddRange(ledger.Violated);
            landed |= ledger.FaultLanded;
            foreach (var (name, count) in ledger.Counts)
            {
                metrics[name] = new((metrics.GetValueOrDefault(name)?.Value ?? 0) + count, "count");
            }
            Console.WriteLine(
                $"  ledger {trial.Name}: {ledger.Checks.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count()} checks"
                    + (ledger.Violated.Count == 0 ? ", no violation" : $", violated: {string.Join(", ", ledger.Violated)}")
            );
            if (ledger.Violated.Count > 0 || faultMustLand)
            {
                foreach (var check in ledger.Checks)
                {
                    Console.WriteLine($"    [{(check.Passed ? "ok  " : "FAIL")}] {check.Code, -24} {check.Identity}: {check.Detail}");
                }
            }
        }
        result = result with { Metrics = metrics, Evidence = evidence };
        if (violated.Count > 0)
        {
            return result with
            {
                Status = "FAIL",
                Safety = "FAIL",
                Reason =
                    $"Ledger checks returned violating rows ({string.Join(", ", violated.Distinct(StringComparer.Ordinal))}); the offending ids are in the trial's ledger evidence."
                    + (result.Reason is null ? "" : " " + result.Reason),
            };
        }
        if (result.Status != "PASS")
        {
            return result;
        }
        if (unavailable is not null)
        {
            return result with { Status = "INCONCLUSIVE", Safety = "UNVERIFIED", Reason = unavailable };
        }
        return faultMustLand && !landed
            ? result with
            {
                Status = "INCONCLUSIVE",
                Coverage = "INCOMPLETE",
                Reason = "The ledger holds no orphaned attempt: no kill landed on an attempt that recovery reclaimed.",
            }
            : result;
    }

    private static async Task<TrialLedger> RunAsync(OwnedProvider trial, bool quiesced, bool faultMustLand, CancellationToken ct)
    {
        await using var connection = await trial.OpenAsync(ct);
        var prefix = trial.Provider == "sqlite" ? "main." : trial.Schema + ".";
        var now = trial.Provider switch
        {
            "sqlite" => "CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)",
            "mssql" => "SYSUTCDATETIME()",
            _ => "now()",
        };
        List<Check> checks = [];
        List<string> violated = [];
        Dictionary<string, double> counts = new(StringComparer.Ordinal);
        var landed = false;
        foreach (var query in Queries)
        {
            if (query.Kind == CheckKind.Quiesced && !quiesced)
            {
                checks.Add(new(query.Name, "trial", true, "Not run: the trial did not resolve its accepted work."));
                continue;
            }
            if (query.Kind == CheckKind.Inverted && !faultMustLand)
            {
                continue;
            }
            var (rows, total) = await ReadAsync(
                connection,
                query.Sql.Replace("{s}", prefix, StringComparison.Ordinal).Replace("{now}", now, StringComparison.Ordinal),
                ct
            );
            if (query.Kind is CheckKind.Measured or CheckKind.Inverted)
            {
                landed |= query.Kind == CheckKind.Inverted && total > 0;
                foreach (var (column, value) in rows.FirstOrDefault() ?? [])
                {
                    counts[column] = value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                checks.Add(
                    new(
                        query.Name,
                        "trial",
                        query.Kind == CheckKind.Measured || total > 0,
                        total > 0 ? Describe(rows[0]) : "No row: no attempt was orphaned."
                    )
                );
                continue;
            }
            if (total == 0)
            {
                checks.Add(new(query.Name, "trial", true, "0 rows."));
                continue;
            }
            violated.Add(query.Name);
            checks.AddRange(
                rows.Select(row => new Check(query.Name, Describe(row), false, "Returned by the check; any row is a violation."))
            );
            if (total > rows.Count)
            {
                checks.Add(new(query.Name, $"{total - rows.Count} further rows", false, "Counted, not recorded."));
            }
        }
        await RateContractAsync(connection, prefix, checks, violated, ct);
        return new([.. checks], violated, landed, counts);
    }

    private static async Task<(List<(string Column, object? Value)[]> Rows, int Total)> ReadAsync(
        DbConnection connection,
        string sql,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(ct);
        List<(string, object?)[]> rows = [];
        var total = 0;
        while (await reader.ReadAsync(ct))
        {
            if (total++ < RecordedRows)
            {
                // Column 0 is the check's own name.
                rows.Add(
                    Enumerable
                        .Range(1, reader.FieldCount - 1)
                        .Select(i => (reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i)))
                        .ToArray()
                );
            }
        }
        return (rows, total);
    }

    private static string Describe((string Column, object? Value)[] row) =>
        string.Join(' ', row.Select(c => $"{c.Column}={Convert.ToString(c.Value, CultureInfo.InvariantCulture) ?? "null"}"));

    /// <summary>
    /// Check 14, the rate contract, for every rate-limited definition against its own budgets: R*(T + W) + B admitted
    /// starts in any window of T seconds, counted from the definition's <c>metered-admitted</c> notes.
    /// </summary>
    private static async Task RateContractAsync(
        DbConnection connection,
        string prefix,
        List<Check> checks,
        List<string> violated,
        CancellationToken ct
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.id, d.name, d.rate_limit_effective, e.created_at_utc
            FROM   {prefix}events e
            JOIN   {prefix}jobs j ON j.id = e.job_id
            JOIN   {prefix}definitions d ON d.id = j.definition_id
            WHERE  e.event_code = 90
              AND  e.reason_message = 'metered-admitted'
              AND  d.rate_limit_effective IS NOT NULL
            ORDER  BY d.id, e.created_at_utc
            """;
        command.CommandTimeout = 0;
        List<(long Definition, string Name, string Rate, DateTime Instant)> notes = [];
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                // SQLite stores an instant as epoch milliseconds; the server providers hand back a DateTime.
                var value = reader.GetValue(3);
                notes.Add(
                    (
                        Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                        reader.GetString(1),
                        reader.GetString(2),
                        value is long ms ? DateTime.UnixEpoch.AddMilliseconds(ms) : Convert.ToDateTime(value, CultureInfo.InvariantCulture)
                    )
                );
            }
        }
        foreach (var meter in notes.GroupBy(n => (n.Definition, n.Name, n.Rate)))
        {
            var instants = meter.Select(n => n.Instant).ToArray();
            var (perSecond, burst, validitySeconds) = Meter(meter.Key.Rate);
            var budget1 = (int)Math.Floor(perSecond * (1 + validitySeconds) + burst);
            var budget10 = (int)Math.Floor(perSecond * (10 + validitySeconds) + burst);
            var (max1, over1) = Scan(instants, 1, budget1);
            var (max10, over10) = Scan(instants, 10, budget10);
            var over = over1.Concat(over10).ToArray();
            checks.Add(
                new(
                    "rate-contract",
                    meter.Key.Name,
                    over.Length == 0,
                    $"rate={meter.Key.Rate} admitted={instants.Length} max_1s={max1} budget_1s={budget1} max_10s={max10} budget_10s={budget10}"
                )
            );
            checks.AddRange(over.Take(RecordedRows).Select(window => new Check("rate-contract", meter.Key.Name, false, window)));
            if (over.Length > 0)
            {
                violated.Add("rate-contract");
            }
        }
    }

    /// <summary>
    /// The meter derived from the declared rate as RateLimitSpec derives it: the emission interval is the period over the
    /// count, rounded up; the burst is one second's worth of the rate, floored, never below one; a booked turn stays
    /// valid for a second past its instant, or one interval when that is longer.
    /// </summary>
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

    /// <summary>
    /// The busiest window of <paramref name="seconds"/> and every window over budget. Each admission opens a window of
    /// its own, so scanning from each covers every window that can hold a maximum; the end pointer only moves forward.
    /// </summary>
    private static (int Max, List<string> Over) Scan(DateTime[] instants, double seconds, int budget)
    {
        var window = TimeSpan.FromSeconds(seconds);
        List<string> over = [];
        var max = 0;
        var end = 0;
        for (var start = 0; start < instants.Length; start++)
        {
            end = Math.Max(end, start);
            while (end < instants.Length && instants[end] - instants[start] < window)
            {
                end++;
            }
            var admitted = end - start;
            max = Math.Max(max, admitted);
            if (admitted > budget)
            {
                over.Add($"{seconds:0}s window from {instants[start]:yyyy-MM-dd HH:mm:ss.fff} admitted {admitted}, budget {budget}");
            }
        }
        return (max, over);
    }

    /// <summary>
    /// Splits certify.sql at statements ending in a bare <c>;</c>; each is named by its first projected literal, and the
    /// marker on the last numbered header before it is its kind.
    /// </summary>
    private static LedgerQuery[] Parse(string file)
    {
        List<LedgerQuery> queries = [];
        foreach (var chunk in file.Split(";\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = chunk.Split('\n');
            var statement = string.Join('\n', lines.Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
            var open = statement.IndexOf('\'', StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(statement) || open < 0)
            {
                continue;
            }
            var header = lines.Last(line => Header().IsMatch(line));
            queries.Add(
                new(
                    statement[(open + 1)..statement.IndexOf('\'', open + 1)],
                    header.Contains("[QUIESCED ONLY]", StringComparison.Ordinal) ? CheckKind.Quiesced
                        : header.Contains("[INVERTED", StringComparison.Ordinal) ? CheckKind.Inverted
                        : header.Contains("[MEASURED", StringComparison.Ordinal) ? CheckKind.Measured
                        : CheckKind.AnyTime,
                    statement
                )
            );
        }
        return [.. queries];
    }

    [GeneratedRegex(@"^-- \d+\. ")]
    private static partial Regex Header();

    private static string ReadCertifySql()
    {
        using var stream =
            typeof(LedgerValidation).Assembly.GetManifestResourceStream("Acta.Bench.Certification.certify.sql")
            ?? throw new InvalidOperationException("Missing certify.sql resource.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}
