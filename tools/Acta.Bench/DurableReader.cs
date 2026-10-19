using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Acta.Bench;

internal sealed record DurableRow(
    long Id,
    string Key,
    long? ParentId,
    int Status,
    string? Output,
    int ExecutionNumber,
    string JobNamespace
);

internal sealed class DurableReader(OwnedProvider provider)
{
    private string Table(string name) => provider.Provider == "sqlite" ? $"main.{name}" : $"{provider.Schema}.{name}";

    public async Task<List<DurableRow>> ReadAsync(IReadOnlyCollection<long>? ids, CancellationToken ct, bool lineage = false)
    {
        List<DurableRow> rows = [];
        await using var c = await provider.OpenAsync(ct);
        long after = 0;
        do
        {
            await using var command = c.CreateCommand();
            var where = "j.id > @after";
            if (ids is not null)
            {
                if (ids.Count == 0)
                {
                    return rows;
                }
                var names = ids.Select((id, i) => Add(command, $"@id{i}", id)).ToArray();
                where += $" AND j.{(lineage ? "lineage_root_id" : "id")} IN ({string.Join(',', names)})";
            }
            Add(command, "@after", after);
            command.CommandText =
                $"SELECT {(provider.Provider == "mssql" ? "TOP (1000)" : "")} j.id, j.deduplication_key, j.parent_id, r.status_code, r.result, r.execution_number, n.name FROM {Table("jobs")} j JOIN {Table("runtimes")} r ON r.job_id=j.id JOIN {Table("namespaces")} n ON n.id=j.namespace_id JOIN {Table("definitions")} d ON d.id=j.definition_id WHERE d.name NOT LIKE 'sys.%' AND {where} ORDER BY j.id {(provider.Provider != "mssql" ? "LIMIT 1000" : "")}";
            var count = 0;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                rows.Add(
                    new(
                        id,
                        reader.IsDBNull(1) ? $"unkeyed-{id}" : reader.GetString(1),
                        reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                        Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                        reader.IsDBNull(4) ? null : Decode((byte[])reader.GetValue(4)),
                        Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                        reader.GetString(6)
                    )
                );
                after = id;
                count++;
            }
            if (count < 1000)
            {
                return rows;
            }
        } while (true);
    }

    public async Task<StepObservation[]> StepsAsync(IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        if (ids is { Count: 0 })
        {
            return [];
        }
        await using var connection = await provider.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var filter = ids is null ? "" : $" AND s.job_id IN ({string.Join(',', ids.Select((id, i) => Add(command, $"@step{i}", id)))})";
        command.CommandText =
            $"SELECT s.job_id, s.name, s.status_code, s.result FROM {Table("steps")} s JOIN {Table("jobs")} j ON j.id=s.job_id JOIN {Table("namespaces")} n ON n.id=j.namespace_id WHERE n.name IN ('bench','bench-other','bench-2'){filter}";
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
                    Stopwatch.GetTimestamp()
                )
            );
        }
        return [.. result];
    }

    public async Task<ObservedOutcome[]> OutcomesAsync(CancellationToken ct, string? excludedPrefix = null)
    {
        var rows = (await ReadAsync(null, ct))
            .Where(r => excludedPrefix is null || !r.Key.StartsWith(excludedPrefix, StringComparison.Ordinal))
            .ToList();
        var byId = rows.ToDictionary(r => r.Id);
        var committedSteps = (await StepsAsync(null, ct)).ToLookup(s => s.JobId);
        var outcomes = new List<ObservedOutcome>();
        foreach (var row in rows)
        {
            var steps = committedSteps[row.Id].Select(s => s.Name + "=" + s.Result).ToArray();
            outcomes.Add(
                new(
                    Key(row),
                    Status(row.Status),
                    row.Output,
                    row.ParentId is { } p && byId.TryGetValue(p, out var parent) ? Key(parent) : null,
                    [.. steps],
                    row.JobNamespace
                )
            );
        }
        return [.. outcomes];

        string Key(DurableRow row)
        {
            if (row.ParentId is null)
                return row.Key;
            List<string> path = [row.Key];
            HashSet<long> ancestors = [row.Id];
            var current = row;
            while (current.ParentId is { } parentId)
            {
                if (!byId.TryGetValue(parentId, out var parent))
                    return $"invalid-parent-{row.Id}";
                if (!ancestors.Add(parent.Id))
                    return $"invalid-cycle-{row.Id}";
                path.Add(parent.Key);
                current = parent;
            }
            path.Reverse();
            return string.Join('/', path);
        }
    }

    public static string Status(int status) => ((JobStatusCode)status).ToString();

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
