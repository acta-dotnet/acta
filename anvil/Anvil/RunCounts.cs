using System.Data.Common;
using System.Globalization;
using Acta;

namespace Anvil;

/// <summary>
/// This run's jobs counted by status in one grouped read over the ids it can own: those above the highest job id when
/// the dashboard booted, in its namespace. The schema keeps every earlier run, so a count by status over the namespace
/// or the table would grow with history on every poll; this one grows only with the run. A result is reused for two
/// seconds, which the rate's ten-second slope does not notice.
/// </summary>
public sealed class RunCounts(AnvilDatabase database, string schema, string namespaceName)
{
    private static readonly TimeSpan Reuse = TimeSpan.FromSeconds(2);

    private readonly string _prefix = LocalDatabase.IsSqlite(database.Provider) ? "" : schema + ".";
    private readonly Lock _gate = new();
    private long _lastIdBeforeRun;
    private int? _namespaceId;
    private DateTime _readAtUtc;
    private RunStatusCounts _counts = new(0, 0, 0, 0, 0);
    private Task<RunStatusCounts>? _reading;

    /// <summary>Fixes the run's lower bound; called once at boot, before any worker or seed can enqueue.</summary>
    public async Task MarkStartAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = Command(connection, $"SELECT COALESCE(MAX(id), 0) FROM {_prefix}jobs");
        _lastIdBeforeRun = Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>The latest counts; pollers arriving while a read is in flight share it.</summary>
    public Task<RunStatusCounts> ReadAsync()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _readAtUtc < Reuse)
            {
                return Task.FromResult(_counts);
            }

            // Started on the pool, so the read clears _reading only after this lock has set it.
            return _reading ??= Task.Run(() => ReadFromDbAsync(CancellationToken.None));
        }
    }

    private async Task<RunStatusCounts> ReadFromDbAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await database.OpenAsync(ct);
            _namespaceId ??= await NamespaceIdAsync(connection, ct);
            if (_namespaceId is not { } namespaceId)
            {
                return _counts;
            }

            await using var command = Command(
                connection,
                // A recurring slot is one reused row per schedule, not work the run enqueued.
                $"""
                SELECT r.status_code, COUNT(*) FROM {_prefix}runtimes r
                WHERE r.job_id > @after AND r.namespace_id = @ns
                    AND NOT EXISTS (SELECT 1 FROM {_prefix}schedules s WHERE s.job_id = r.job_id)
                GROUP BY r.status_code
                """,
                ("@after", _lastIdBeforeRun),
                ("@ns", namespaceId)
            );
            long total = 0,
                ready = 0,
                executing = 0,
                succeeded = 0,
                failed = 0;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var rows = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                total += rows;
                switch ((JobStatusCode)Convert.ToByte(reader.GetValue(0), CultureInfo.InvariantCulture))
                {
                    case JobStatusCode.Ready:
                        ready = rows;
                        break;
                    case JobStatusCode.Executing:
                        executing = rows;
                        break;
                    case JobStatusCode.Succeeded:
                        succeeded = rows;
                        break;
                    case JobStatusCode.Failed:
                        failed = rows;
                        break;
                }
            }

            var counts = new RunStatusCounts(total, ready, executing, succeeded, failed);
            lock (_gate)
            {
                _counts = counts;
                _readAtUtc = DateTime.UtcNow;
            }
            return counts;
        }
        finally
        {
            lock (_gate)
            {
                _reading = null;
            }
        }
    }

    private async Task<int?> NamespaceIdAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = Command(connection, $"SELECT id FROM {_prefix}namespaces WHERE name = @name", ("@name", namespaceName));
        return await command.ExecuteScalarAsync(ct) is { } id and not DBNull ? Convert.ToInt32(id, CultureInfo.InvariantCulture) : null;
    }

    private static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}

/// <summary>One run's jobs by status: every one of them, then the four the cockpit shows.</summary>
public sealed record RunStatusCounts(long Total, long Ready, long Executing, long Succeeded, long Failed);
