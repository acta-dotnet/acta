using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Acta.Verify;

/// <summary>A job's durable row; <c>Modified</c> is its last state change in database ticks.</summary>
internal sealed record DurableRow(
    long JobId,
    string Key,
    long? ParentJobId,
    int Status,
    string? Output,
    int ExecutionNumber,
    string JobNamespace,
    long? LineageRootJobId,
    long Modified
);

/// <summary>
/// A database instant, in 100-nanosecond database ticks, paired with the stopwatch tick at the midpoint of the round trip
/// that read it; the pairing is exact to within half that round trip.
/// </summary>
internal sealed record DatabaseClock(long Ticks, long DatabaseTicks, long RoundTripTicks)
{
    /// <summary>
    /// The stopwatch tick of a database instant. The product is 128-bit: at a 1 GHz stopwatch a 64-bit one overflows
    /// once the instant is some fifteen minutes from the pairing.
    /// </summary>
    public long ToStopwatch(long databaseTicks) =>
        Ticks + (long)((Int128)(databaseTicks - DatabaseTicks) * Stopwatch.Frequency / TimeSpan.TicksPerSecond);
}

internal sealed class DurableReader(OwnedProvider provider)
{
    private const int Page = 10_000;

    /// <summary>The predicates of the claim and in-flight partial indexes: ready or timed, and executing.</summary>
    private static readonly string[] IndexedLiveStates =
    [
        "cs.job_status_code IN (10, 20) AND cs.next_run_at_utc IS NOT NULL",
        "cs.job_status_code = 50 AND cs.leased_by_worker_id IS NOT NULL",
    ];

    private string Table(string name) => provider.Provider == "sqlite" ? $"main.{name}" : $"{provider.Schema}.{name}";

    /// <summary>
    /// Every non-system job above <paramref name="afterJobId"/> up to <paramref name="throughJobId"/>, or those of
    /// <paramref name="ids"/>, read in job-id pages.
    /// </summary>
    public async Task<List<DurableRow>> ReadAsync(
        IReadOnlyCollection<long>? ids,
        CancellationToken ct,
        long afterJobId = 0,
        long throughJobId = long.MaxValue
    )
    {
        List<DurableRow> rows = [];
        if (ids is { Count: 0 })
        {
            return rows;
        }
        await foreach (var page in PagesAsync(afterJobId, throughJobId, false, ids, ct))
        {
            rows.AddRange(page);
        }
        return rows;
    }

    /// <summary>
    /// Every non-system root above <paramref name="afterJobId"/>, a job-id page at a time, so a caller holds one page of
    /// rows however large the trial.
    /// </summary>
    public IAsyncEnumerable<List<DurableRow>> RootPagesAsync(long afterJobId, CancellationToken ct) =>
        PagesAsync(afterJobId, long.MaxValue, true, null, ct);

    /// <summary>
    /// The descendants of the non-system jobs above <paramref name="afterJobId"/>, those that succeeded, and the
    /// workload's committed checkpoints, in one round trip.
    /// </summary>
    public async Task<(long Descendants, long Succeeded, long Steps)> TreeTotalsAsync(long afterJobId, CancellationToken ct)
    {
        await using var connection = await provider.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        Add(command, "@after", afterJobId);
        var jobs = Table("jobs");
        var states = Table("jobs_state");
        var definitions = Table("definitions");
        var steps = Table("steps");
        command.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN r.job_status_code = 100 THEN 1 ELSE 0 END), 0),
                   (SELECT COUNT(*) FROM {steps} s JOIN {jobs} sj ON sj.job_id = s.job_id JOIN {definitions} sd ON sd.definition_id = sj.definition_id
                    WHERE sj.job_id > @after AND sd.definition_name NOT LIKE 'sys.%' AND s.step_status_code = 100
                      AND (s.step_name LIKE 'step-%' OR s.step_name = 'effect'))
            FROM {jobs} j JOIN {definitions} d ON d.definition_id = j.definition_id LEFT JOIN {states} r ON r.job_id = j.job_id
            WHERE j.job_id > @after AND j.parent_job_id IS NOT NULL AND d.definition_name NOT LIKE 'sys.%'
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)
        );
    }

    private async IAsyncEnumerable<List<DurableRow>> PagesAsync(
        long afterJobId,
        long throughJobId,
        bool rootsOnly,
        IReadOnlyCollection<long>? ids,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        await using var c = await provider.OpenAsync(ct);
        var after = afterJobId;
        while (true)
        {
            var page = await PageAsync(c, after, throughJobId, rootsOnly, ids, ct);
            if (page.Count > 0)
            {
                yield return page;
            }
            if (page.Count < Page)
            {
                yield break;
            }
            after = page[^1].JobId;
        }
    }

    private async Task<List<DurableRow>> PageAsync(
        DbConnection c,
        long after,
        long through,
        bool rootsOnly,
        IReadOnlyCollection<long>? ids,
        CancellationToken ct
    )
    {
        await using var command = c.CreateCommand();
        var where = $"j.job_id > {Add(command, "@after", after)} AND j.job_id <= {Add(command, "@through", through)}";
        if (rootsOnly)
        {
            where += " AND j.parent_job_id IS NULL";
        }
        if (ids is not null)
        {
            var names = ids.Select((id, i) => Add(command, $"@id{i}", id)).ToArray();
            where += $" AND j.job_id IN ({string.Join(',', names)})";
        }
        command.CommandText =
            $"SELECT {(provider.Provider == "mssql" ? $"TOP ({Page})" : "")} j.job_id, j.deduplication_key, j.parent_job_id, r.job_status_code, r.result, r.execution_number, n.namespace_name, j.lineage_root_job_id, r.modified_at_utc FROM {Table("jobs")} j JOIN {Table("jobs_state")} r ON r.job_id=j.job_id JOIN {Table("namespaces")} n ON n.namespace_id=j.namespace_id JOIN {Table("definitions")} d ON d.definition_id=j.definition_id WHERE d.definition_name NOT LIKE 'sys.%' AND {where} ORDER BY j.job_id {(provider.Provider != "mssql" ? $"LIMIT {Page}" : "")}";
        List<DurableRow> rows = new(Page);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var jobId = reader.GetInt64(0);
            rows.Add(
                new(
                    jobId,
                    reader.IsDBNull(1) ? $"unkeyed-{jobId}" : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    // A status is a tinyint on SQL Server and a smallint elsewhere.
                    Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? null : Decode((byte[])reader.GetValue(4)),
                    reader.GetInt32(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    DatabaseTicks(reader.GetValue(8))
                )
            );
        }
        return rows;
    }

    /// <summary>The steps of every workload job above <paramref name="afterJobId"/>, or of <paramref name="ids"/>.</summary>
    public async Task<StepObservation[]> StepsAsync(IReadOnlyCollection<long>? ids, CancellationToken ct, long afterJobId = 0)
    {
        if (ids is { Count: 0 })
        {
            return [];
        }
        await using var connection = await provider.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var filter = ids is null ? "" : $" AND s.job_id IN ({string.Join(',', ids.Select((id, i) => Add(command, $"@step{i}", id)))})";
        command.CommandText =
            $"SELECT s.job_id, s.step_name, s.step_status_code, s.result, s.modified_at_utc FROM {Table("steps")} s JOIN {Table("jobs")} j ON j.job_id=s.job_id JOIN {Table("namespaces")} n ON n.namespace_id=j.namespace_id WHERE n.namespace_name IN ('verify','verify-other','verify-2') AND s.job_id > {Add(command, "@after", afterJobId)}{filter}";
        List<StepObservation> result = [];
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var status = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
            result.Add(
                new(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.GetString(1),
                    status,
                    status == 100
                        ? reader.IsDBNull(3)
                            ? "void"
                            : Decode((byte[])reader.GetValue(3))
                        : "uncommitted",
                    DatabaseTicks(reader.GetValue(4))
                )
            );
        }
        return [.. result];
    }

    public async Task<ObservedOutcome[]> OutcomesAsync(CancellationToken ct) =>
        Outcomes(await ReadAsync(null, ct), await StepsAsync(null, ct));

    /// <summary>The logical outcome of each row: its key path from its root, status, output, parent, and committed steps.</summary>
    public static ObservedOutcome[] Outcomes(IReadOnlyList<DurableRow> rows, IEnumerable<StepObservation> steps)
    {
        var byId = rows.ToDictionary(r => r.JobId);
        var committedSteps = steps.ToLookup(s => s.JobId);
        var outcomes = new List<ObservedOutcome>();
        foreach (var row in rows)
        {
            outcomes.Add(
                new(
                    Key(row),
                    Status(row.Status),
                    row.Output,
                    row.ParentJobId is { } p && byId.TryGetValue(p, out var parent) ? Key(parent) : null,
                    [.. committedSteps[row.JobId].Select(s => s.Name + "=" + s.Result)],
                    row.JobNamespace
                )
            );
        }
        return [.. outcomes];

        string Key(DurableRow row)
        {
            if (row.ParentJobId is null)
                return row.Key;
            List<string> path = [row.Key];
            HashSet<long> ancestors = [row.JobId];
            var current = row;
            while (current.ParentJobId is { } parentId)
            {
                if (!byId.TryGetValue(parentId, out var parent))
                    return $"invalid-parent-{row.JobId}";
                if (!ancestors.Add(parent.JobId))
                    return $"invalid-cycle-{row.JobId}";
                path.Add(parent.Key);
                current = parent;
            }
            path.Reverse();
            return string.Join('/', path);
        }
    }

    /// <summary>
    /// Pairs the database clock with the stopwatch: of a few reads of the database's current instant, the one with the
    /// shortest round trip, against that round trip's midpoint.
    /// </summary>
    public async Task<DatabaseClock> ClockAsync(CancellationToken ct)
    {
        await using var connection = await provider.OpenAsync(ct);
        DatabaseClock? best = null;
        for (var i = 0; i < 9; i++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = provider.Provider switch
            {
                "sqlite" => "SELECT CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)",
                "mssql" => "SELECT SYSUTCDATETIME()",
                _ => "SELECT clock_timestamp()",
            };
            var sent = Stopwatch.GetTimestamp();
            var instant = DatabaseTicks(await command.ExecuteScalarAsync(ct));
            var received = Stopwatch.GetTimestamp();
            if (best is null || received - sent < best.RoundTripTicks)
            {
                best = new(sent + (received - sent) / 2, instant, received - sent);
            }
        }
        return best!;
    }

    /// <summary>The highest job id the schema has issued, zero when it holds no job.</summary>
    public async Task<long> LastJobIdAsync(CancellationToken ct)
    {
        await using var connection = await provider.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COALESCE(MAX(job_id), 0) FROM {Table("jobs")}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Unfinished non-system roots from job id <paramref name="from"/>, and the lowest of their ids. A root finishes once,
    /// so a later count can start from that id.
    /// </summary>
    public async Task<(long Count, long Lowest)> UnfinishedRootsAsync(long from, CancellationToken ct)
    {
        await using var connection = await provider.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*), MIN(j.job_id) FROM {Table("jobs")} j JOIN {Table("jobs_state")} r ON r.job_id=j.job_id JOIN {Table("definitions")} d ON d.definition_id=j.definition_id WHERE j.job_id >= {Add(command, "@from", from)} AND j.parent_job_id IS NULL AND r.job_status_code NOT IN (100, 200, 220) AND d.definition_name NOT LIKE 'sys.%'";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? from : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)
        );
    }

    /// <summary>
    /// A failed check for each descendant above <paramref name="after"/> still live under a terminal root, skipping those
    /// already in <paramref name="reported"/>. A root waits for its descendants, so the violation is visible only while
    /// the descendant runs: a final read misses it once the descendant finishes too.
    /// </summary>
    public async Task<Check[]> LiveChildChecksAsync(long after, HashSet<long> reported, CancellationToken ct) =>
        [
            .. (await LiveDescendantsAsync(after, ct))
                .Where(d => reported.Add(d.Descendant))
                .Select(d => new Check(
                    "terminal-parent-live-child",
                    d.RootKey,
                    false,
                    $"Root job {d.Root} reached a terminal outcome while its descendant job {d.Descendant} was not terminal."
                )),
        ];

    /// <summary>
    /// Ready, timed or executing descendants above <paramref name="after"/> whose root is already terminal, with that
    /// root's id and key, read in one statement through the claim and in-flight partial indexes.
    /// </summary>
    private async Task<List<(long Root, string RootKey, long Descendant)>> LiveDescendantsAsync(long after, CancellationToken ct)
    {
        await using var connection = await provider.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        Add(command, "@after", after);
        command.CommandText = string.Join(
            " UNION ALL ",
            IndexedLiveStates.Select(state =>
                $"SELECT r.job_id, r.deduplication_key, c.job_id FROM {Table("jobs_state")} cs JOIN {Table("jobs")} c ON c.job_id = cs.job_id JOIN {Table("jobs_state")} rs ON rs.job_id = c.lineage_root_job_id JOIN {Table("jobs")} r ON r.job_id = rs.job_id WHERE {state} AND cs.job_id > @after AND c.parent_job_id IS NOT NULL AND rs.job_status_code IN (100, 200, 220)"
            )
        );
        List<(long, string, long)> found = [];
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var root = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            found.Add(
                (
                    root,
                    reader.IsDBNull(1) ? $"unkeyed-{root}" : reader.GetString(1),
                    Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)
                )
            );
        }
        return found;
    }

    /// <summary>
    /// Ready or timed and executing jobs in the job-id range <paramref name="first"/>..<paramref name="last"/>, read from
    /// the claim and in-flight partial indexes, which shrink as a drain proceeds. One statement reads both, so a job moving
    /// from one to the other counts once.
    /// </summary>
    public async Task<long> RemainingCountAsync(long first, long last, CancellationToken ct)
    {
        await using var connection = await provider.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        Add(command, "@first", first);
        Add(command, "@last", last);
        command.CommandText = $"""
            SELECT (SELECT COUNT(*) FROM {Table("jobs_state")}
                    WHERE job_status_code IN (10, 20) AND next_run_at_utc IS NOT NULL AND job_id BETWEEN @first AND @last)
                 + (SELECT COUNT(*) FROM {Table("jobs_state")}
                    WHERE job_status_code = 50 AND leased_by_worker_id IS NOT NULL AND job_id BETWEEN @first AND @last)
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Jobs of any definition in the job-id range <paramref name="first"/>..<paramref name="last"/>.</summary>
    public async Task<long> RangeCountAsync(long first, long last, CancellationToken ct)
    {
        await using var connection = await provider.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM {Table("jobs")} WHERE job_id BETWEEN {Add(command, "@first", first)} AND {Add(command, "@last", last)}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public static string Status(int status) => ((JobStatusCode)status).ToString();

    /// <summary>A stored instant in 100-nanosecond ticks: SQLite stores epoch milliseconds, the servers return a DateTime.</summary>
    private static long DatabaseTicks(object? value) =>
        value is DateTime instant ? instant.Ticks : Convert.ToInt64(value, CultureInfo.InvariantCulture) * TimeSpan.TicksPerMillisecond;

    private static string Decode(byte[] value)
    {
        var text = Encoding.UTF8.GetString(value);
        return text.StartsWith('"') ? JsonSerializer.Deserialize(text, WorkJson.Default.String) ?? "" : text;
    }

    private static string Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return name;
    }
}
