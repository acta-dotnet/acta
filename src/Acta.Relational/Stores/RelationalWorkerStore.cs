using System.Globalization;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Modules.Execution.Workers;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IWorkerStore"/> over <see cref="IDbSession"/>. One implementation for
/// every SQL provider: the worker list read and the lifecycle writes are written once, and provider
/// differences live behind the session (routine vs inline, transaction, result-set selection) and the
/// dialect (parameter creation).
/// </summary>
internal sealed class RelationalWorkerStore(IDbSession session, ISqlDialect dialect) : IWorkerStore
{
    public async Task<StartWorkerRow> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Execution", "Workers/StartWorker"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.Name, command.NamespaceName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.OwnerTeam, command.OwnerTeam));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.Description, command.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.CatalogHash, command.CatalogHash));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.StatusCode, NamespaceStatusCode.Active));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.DeploymentVersion, command.DeploymentVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.Host, command.HostName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.EngineVersion, command.EngineVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.DotnetVersion, command.DotnetVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.ProcessId, command.ProcessId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.MaxConcurrency, command.MaxConcurrency));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.WorkerRef, command.WorkerRef));
            },
            DbProjectionResolver.Resolve<StartWorkerRow>(),
            ct
        );

        return rows.Count > 0 ? rows[^1] : throw new InvalidOperationException("start_worker returned no namespace/worker id row.");
    }

    public Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Execution", "Workers/StopWorker"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, namespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.WorkerId, workerId));
            },
            ct
        );

    public Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(int workerId, int leaseTtlSeconds, bool draining, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Execution", "Workers/ExtendWorkerLeases"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, workerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, leaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.Draining, draining));
            },
            reader => reader.GetInt64(0),
            ct
        );

    public async Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct)
    {
        var counts = await session.ExecuteAsync(
            new StoreCommand("Execution", "Workers/MarkDeadWorkers"),
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.DeadAfterSeconds, deadAfterSeconds)),
            reader => reader.IsDBNull(0) ? (int?)null : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            ct
        );

        return counts.Count > 0 && counts[^1] is int count
            ? count
            : throw new InvalidOperationException("mark_dead_workers returned no count.");
    }

    public async ValueTask<WorkerDetail?> GetWorkerAsync(Guid workerRef, CancellationToken ct) =>
        await session.QueryAsync<WorkerDetail?>(
            "Sql/Execution/Workers/GetWorker.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.WorkerRef, workerRef)),
            async (reader, token) =>
                await reader.ReadAsync(token) ? DbProjectionResolver.Resolve<JobWorkerListRow>()(reader).ToDetail() : null,
            ct
        );

    public Task<WorkerPage> ListWorkersAsync(WorkerPageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Execution/Workers/ListWorkers.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, request.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobWorker.StatusCode, request.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorLastSeenAtUtc, request.CursorLastSeenAtUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorIntId, request.CursorId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, request.Take));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<JobWorkerListRow>();
                var rows = new List<WorkerListItem>(request.Take);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader).ToItem());
                }

                long? total = null;
                if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                {
                    total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }

                return new WorkerPage(rows, total);
            },
            ct
        );
}
