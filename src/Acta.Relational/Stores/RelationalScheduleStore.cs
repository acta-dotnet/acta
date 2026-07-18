using System.Data.Common;
using System.Globalization;
using Acta.Features.Jobs;
using Acta.Features.Schedules;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IScheduleStore"/> over <see cref="IDbSession"/>: the schedule reads
/// and list, the whole-namespace slot + schedule registration batch, and the control verbs. Provider
/// mechanics (routine vs inline, bulk-shape binding) live behind the session and the dialect.
/// </summary>
internal sealed class RelationalScheduleStore(IDbSession session, ISqlDialect dialect) : IScheduleStore
{
    public Task<IReadOnlyList<LiveSchedule>> GetLiveSchedulesAsync(long jobId, CancellationToken ct) =>
        session.QueryAsync<IReadOnlyList<LiveSchedule>>(
            "Features/Schedules/Sql/GetLiveSchedules.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.JobId, jobId))),
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<LiveSchedule>();
                var rows = new List<LiveSchedule>();
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader));
                }

                return rows;
            },
            ct
        );

    public Task<IReadOnlyList<StoredScheduleState>> GetScheduleStateAsync(short namespaceId, CancellationToken ct) =>
        session.QueryAsync<IReadOnlyList<StoredScheduleState>>(
            "Features/Schedules/Sql/GetScheduleState.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.NamespaceId, namespaceId))),
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<StoredScheduleState>();
                var rows = new List<StoredScheduleState>();
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader));
                }

                return rows;
            },
            ct
        );

    public Task<SchedulePage> ListJobSchedulesAsync(SchedulePageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Features/Schedules/Sql/ListJobSchedules.sql",
            cmd => AddListParameters(cmd, request),
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<JobScheduleListRow>();
                var rows = new List<JobScheduleListItem>(request.Take);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader).ToItem());
                }

                long? total = null;
                if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                {
                    total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }

                return new SchedulePage(rows, total);
            },
            ct
        );

    public Task<IReadOnlyList<RegisteredScheduleSlot>> RegisterScheduledJobsAsync(
        RegisterScheduledJobsCommand command,
        CancellationToken ct
    ) =>
        session.ExecuteAsync(
            new StoreCommand("Schedules", "RegisterScheduledJobs"),
            cmd => dialect.BindRegisterScheduledJobs(cmd, command.Definitions, command.SlotRefs, session.Schema),
            DbProjectionResolver.Resolve<RegisteredScheduleSlot>(),
            ct
        );

    public Task<ScheduleControlOutcome> PauseScheduleAsync(PauseScheduleCommand command, CancellationToken ct) =>
        ControlAsync(
            "PauseSchedule",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.JobId, command.JobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.Name, command.ScheduleName)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.PausedUntilUtc, command.PausedUntilUtc)));
                AddControlTailParameters(cmd, command.JobNextRunAtUtc, command.Actor, command.Note);
            },
            ct
        );

    public Task<ScheduleControlOutcome> ResumeScheduleAsync(ResumeScheduleCommand command, CancellationToken ct) =>
        ControlAsync(
            "ResumeSchedule",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.JobId, command.JobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.Name, command.ScheduleName)));
                cmd.Parameters.Add(
                    dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.NextRunAtUtc, command.ScheduleNextRunAtUtc))
                );
                AddControlTailParameters(cmd, command.JobNextRunAtUtc, command.Actor, command.Note);
            },
            ct
        );

    public Task<ScheduleControlOutcome> SetScheduleOverridesAsync(SetScheduleOverridesCommand command, CancellationToken ct) =>
        ControlAsync(
            "SetScheduleOverrides",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.JobId, command.JobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.Name, command.ScheduleName)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.ExpectedScheduleVersion, command.ExpectedVersion)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.ScheduleExpressionOverride, command.Expression)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.ScheduleTimeZoneIdOverride, command.TimeZoneId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.Note, command.Note)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.JobNextRunAtUtc, command.JobNextRunAtUtc)));
                cmd.Parameters.Add(
                    dialect.CreateParameter(DbParams.For(ActaSchema.Sql.ScheduleNextRunAtUtc, command.ScheduleNextRunAtUtc))
                );
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ActorCode, command.Actor.ActorCode)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ActorKey, command.Actor.ActorKey)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage)));
            },
            ct
        );

    public Task<ScheduleControlOutcome> TriggerScheduleNowAsync(TriggerScheduleNowCommand command, CancellationToken ct) =>
        ControlAsync(
            "TriggerScheduleNow",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.JobId, command.JobId)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.Name, command.ScheduleName)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ActorCode, command.Actor.ActorCode)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ActorKey, command.Actor.ActorKey)));
                cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage)));
            },
            ct
        );

    private async Task<ScheduleControlOutcome> ControlAsync(string operation, Action<DbCommand> bind, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Schedules", operation),
            bind,
            DbProjectionResolver.Resolve<ScheduleControlOutcome>(),
            ct
        )
        ?? throw new InvalidOperationException($"Control command '{operation}' returned no rows; it must return exactly one outcome row.");

    private void AddListParameters(DbCommand cmd, SchedulePageRequest request)
    {
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.NamespaceFilter, request.JobNamespace)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.JobNameFilter, request.JobName)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.OriginCode, request.Origin)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.LiveOnlyFlag, request.LiveOnly)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.CursorNextRunAtUtc, request.CursorNextRunAtUtc)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.CursorId, request.CursorId)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.PageTake, request.Take)));
        cmd.Parameters.Add(
            dialect.CreateParameter(DbParams.For(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null))
        );
    }

    private void AddControlTailParameters(DbCommand cmd, DateTime? jobNextRunAtUtc, JobControlActor actor, string? note)
    {
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.Sql.JobNextRunAtUtc, jobNextRunAtUtc)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ActorCode, actor.ActorCode)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobEvent.ActorKey, actor.ActorKey)));
        cmd.Parameters.Add(dialect.CreateParameter(DbParams.For(ActaSchema.JobSchedule.Note, note)));
    }
}
