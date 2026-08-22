using System.Data;
using System.Data.Common;
using System.Globalization;
using Acta;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Anvil.Burst;

/// <summary>
/// The three row operations the certification needs and no public Acta API exposes: reading the numeric
/// namespace id, counting the alertable events inside a cursor range, and moving a stored instant
/// backwards (events past the projection horizon, alerts past the retention window).
/// </summary>
/// <remarks>
/// <para>
/// Aging rows is a lab capability, not a product one - Acta has no "pretend this row is older" verb and
/// should not grow one - so a certification that wants to observe a horizon or a retention window without
/// waiting out real days has to write the instant itself. That is the same thing
/// <c>AlertTestOps.AgeEventsPastHorizonAsync</c> does for the conformance suite, one layer lower because
/// this harness lives outside the assembly that can see <c>IDbSession</c>.
/// </para>
/// <para>
/// The event count is a read-only mirror of <c>GetAlertableEvents.sql</c>'s classification, minus its
/// horizon predicate (the harness has already aged every event past the horizon before it counts). It
/// exists so "how many events did that invocation project" is an exact number rather than an inference
/// from how many alert rows appeared. It has to track that file: if the set of alertable transitions
/// changes there and not here, this harness will under- or over-count a drain and say so on a verdict
/// line that reads green.
/// </para>
/// </remarks>
internal sealed class BurstDb(string provider, string schema)
{
    private static readonly IConfiguration s_configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

    private readonly string _provider = provider;
    private readonly string _connectionString = LocalDatabase.ResolveConnectionString(s_configuration, provider, schema);

    // SQLite puts every table in the connection's own database file; the server providers qualify.
    private readonly string _prefix = LocalDatabase.IsSqlite(provider) ? "" : schema + ".";

    /// <summary>The numeric namespace id the events and alerts tables carry, by namespace name.</summary>
    public async Task<short> NamespaceIdAsync(string namespaceName, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM {_prefix}namespaces WHERE name = @p_name";
        AddText(command, "@p_name", namespaceName);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull
            ? throw new InvalidOperationException($"Namespace '{namespaceName}' has no row in {_prefix}namespaces yet.")
            : Convert.ToInt16(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Alertable events of the named definitions with an id in <c>(exclusiveLow, inclusiveHigh]</c>:
    /// exactly the backlog work one generate pass that moved its cursor from the first to the second got
    /// through.
    /// </summary>
    /// <remarks>
    /// Scoped to the workload's own definitions, and it has to be. Every completed <c>sys.alerts</c>
    /// invocation writes its own <c>job.execution-finished</c> event, and a Succeeded execution status is
    /// alertable by the same predicate - so on a long drain the projector starts projecting the
    /// certification's own ticks once they age past the horizon, and an unscoped count would report a
    /// backlog larger than the one that was seeded.
    /// </remarks>
    public async Task<int> CountAlertableEventsAsync(
        short namespaceId,
        IReadOnlyList<string> definitionNames,
        long exclusiveLow,
        long inclusiveHigh,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(definitionNames);
        if (inclusiveHigh <= exclusiveLow || definitionNames.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var names = new string[definitionNames.Count];
        for (var i = 0; i < definitionNames.Count; i++)
        {
            names[i] = "@p_definition_" + i.ToString(CultureInfo.InvariantCulture);
            AddText(command, names[i], definitionNames[i]);
        }

        // Mirrors GetAlertableEvents.sql, minus its horizon predicate (the harness ages every event past
        // the horizon before it counts). The numeric literals are the same closed taxonomies that file
        // spells out: event_code 41 is job.execution-finished, to_status 200/10/20 are Failed/Ready/
        // Suspended, reason 20/21/22 are unhandled exception / lease expired / execution timeout, and
        // execution_status 100 is Succeeded.
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM {_prefix}events e
            INNER JOIN {_prefix}definitions d ON d.id = e.definition_id
            WHERE
                e.namespace_id = @p_namespace_id
                AND d.namespace_id = @p_namespace_id
                AND d.name IN ({string.Join(", ", names)})
                AND e.id > @p_low_event_id
                AND e.id <= @p_high_event_id
                AND e.job_id IS NOT NULL
                AND e.event_code = 41
                AND (
                    e.to_status_code = 200
                    OR (e.to_status_code = 10 AND e.reason_code IN (20, 21, 22))
                    OR (e.to_status_code = 20 AND e.reason_code = 21)
                    OR e.execution_status_code = 100
                )
            """;
        AddInt64(command, "@p_namespace_id", namespaceId);
        AddInt64(command, "@p_low_event_id", exclusiveLow);
        AddInt64(command, "@p_high_event_id", inclusiveHigh);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Backdates every event in the namespace by <paramref name="age"/> so the projection read's safe
    /// horizon admits them all. Returns the rows aged.
    /// </summary>
    public async Task<int> AgeEventsAsync(short namespaceId, TimeSpan age, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {_prefix}events SET created_at_utc = @p_created_at_utc WHERE namespace_id = @p_namespace_id";
        AddInstant(command, "@p_created_at_utc", DateTime.UtcNow - age);
        AddInt64(command, "@p_namespace_id", namespaceId);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Backdates the named alert rows by <paramref name="age"/> so the retention sweep's window has
    /// passed for them. Addressed by numeric id rather than by ref: an id is a plain integer on every
    /// provider, while the ref is a uuid each provider stores in its own shape.
    /// </summary>
    public async Task<int> AgeAlertsAsync(IReadOnlyList<long> alertIds, TimeSpan age, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alertIds);
        if (alertIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var names = new string[alertIds.Count];
        for (var i = 0; i < alertIds.Count; i++)
        {
            names[i] = "@p_id_" + i.ToString(CultureInfo.InvariantCulture);
            AddInt64(command, names[i], alertIds[i]);
        }

        command.CommandText = $"UPDATE {_prefix}alerts SET created_at_utc = @p_created_at_utc WHERE id IN ({string.Join(", ", names)})";
        AddInstant(command, "@p_created_at_utc", DateTime.UtcNow - age);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        DbConnection connection =
            LocalDatabase.IsSqlite(_provider) ? new SqliteConnection(_connectionString)
            : LocalDatabase.IsSqlServer(_provider) ? new SqlConnection(_connectionString)
            : new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        if (LocalDatabase.IsSqlite(_provider))
        {
            // SQLite takes one writer at a time and the harness worker is holding the file. Without a busy
            // timeout an aging UPDATE that lands beside a claim fails outright instead of waiting the
            // fraction of a second it takes for the other write to commit.
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout = 30000;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        return connection;
    }

    private static void AddText(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddInt64(DbCommand command, string name, long value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // SQLite stores instants as epoch milliseconds (INTEGER) so numeric order is chronological order; the
    // server providers store a real timestamp. SQL Server needs DateTime2 named explicitly, because the
    // inferred DateTime maps to the older, coarser type.
    private void AddInstant(DbCommand command, string name, DateTime utc)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        if (LocalDatabase.IsSqlite(_provider))
        {
            parameter.Value = (long)(utc - DateTime.UnixEpoch).TotalMilliseconds;
        }
        else
        {
            if (LocalDatabase.IsSqlServer(_provider))
            {
                parameter.DbType = DbType.DateTime2;
            }
            parameter.Value = utc;
        }
        command.Parameters.Add(parameter);
    }
}
