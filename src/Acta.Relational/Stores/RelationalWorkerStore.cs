using System.Globalization;
using Acta.Features.Workers;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;

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
            new StoreCommand("Workers", "StartWorker"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobNamespace.Name, command.NamespaceName)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobNamespace.OwnerTeam, command.OwnerTeam)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobNamespace.Description, command.Description)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobNamespace.CatalogHash, command.CatalogHash)));
                cmd.Parameters.Add(
                    dialect.CreateParameter(DbParams.For(ActaSchema.JobNamespace.StatusCode, JobNamespaceStatusCode.Active))
                );
                cmd.Parameters.Add(
                    dialect.CreateParameter(DbParams.For(ActaSchema.JobWorker.DeploymentVersion, command.DeploymentVersion))
                );
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobWorker.Host, command.HostName)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobWorker.EngineVersion, command.EngineVersion)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobWorker.DotnetVersion, command.DotnetVersion)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobWorker.ProcessId, command.ProcessId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobWorker.MaxConcurrency, command.MaxConcurrency)));
            },
            DbProjectionResolver.Resolve<StartWorkerRow>(),
            ct
        );

        return rows.Count > 0 ? rows[^1] : throw new InvalidOperationException("start_worker returned no namespace/worker id row.");
    }

    public Task StopWorkerAsync(short namespaceId, int workerId, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Workers", "StopWorker"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Job.NamespaceId, namespaceId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.WorkerId, workerId)));
            },
            ct
        );

    public Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(int workerId, int leaseTtlSeconds, bool draining, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("Workers", "ExtendWorkerLeases"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.LeasedByWorkerId, workerId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.LeaseTtlSeconds, leaseTtlSeconds)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.Draining, draining)));
            },
            reader => reader.GetInt64(0),
            ct
        );

    public async Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct)
    {
        var counts = await session.ExecuteAsync(
            new StoreCommand("Workers", "MarkDeadWorkers"),
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.DeadAfterSeconds, deadAfterSeconds))),
            reader => reader.IsDBNull(0) ? (int?)null : Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            ct
        );

        return counts.Count > 0 && counts[^1] is int count
            ? count
            : throw new InvalidOperationException("mark_dead_workers returned no count.");
    }

    public async ValueTask<JobWorkerDetail?> GetWorkerAsync(int workerId, CancellationToken ct) =>
        await session.QueryAsync<JobWorkerDetail?>(
            "Features/Workers/Sql/GetWorker.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobWorker.Id, workerId))),
            async (reader, token) =>
                await reader.ReadAsync(token) ? DbProjectionResolver.Resolve<JobWorkerListRow>()(reader).ToDetail() : null,
            ct
        );

    public Task<WorkerPage> ListWorkersAsync(WorkerPageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Features/Workers/Sql/ListWorkers.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.NamespaceFilter, request.JobNamespace)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobWorker.StatusCode, request.Status)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.CursorLastSeenAtUtc, request.CursorLastSeenAtUtc)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.CursorIntId, request.CursorId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.PageTake, request.Take)));
                cmd.Parameters.Add(
                    dialect.CreateParameter(DbParams.For(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null))
                );
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<JobWorkerListRow>();
                var rows = new List<JobWorkerListItem>(request.Take);
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
