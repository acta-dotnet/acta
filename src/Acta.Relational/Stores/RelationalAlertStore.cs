using System.Data.Common;
using System.Globalization;
using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Modules.Alerting;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <see cref="IAlertStore"/> over <see cref="IDbSession"/>. One implementation for
/// every SQL provider: the incident-identity raise, the generate/deliver reads, the delivery-outcome and
/// auto-resolve writes, the operator acknowledge/resolve verbs, and the paged list are written once.
/// Provider differences live behind the session (routine vs inline, result-set selection) and the
/// dialect (parameter creation). The delivery-outcome and auto-resolve writes are inline SQL in every
/// provider (no routine), so they load by literal path through the session's read seam.
/// </summary>
internal sealed class RelationalAlertStore(IDbSession session, ISqlDialect dialect) : IAlertStore
{
    public async Task<AlertRaiseOutcome> RaiseJobAlertAsync(RaiseJobAlertCommand command, CancellationToken ct)
    {
        try
        {
            var rows = await session.ExecuteAsync(
                new StoreCommand("Alerting", "RaiseJobAlert"),
                cmd => AddRaiseParameters(cmd, command),
                reader =>
                    reader.IsDBNull(0)
                        ? null
                        : new AlertRaiseOutcome(
                            Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                            reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)
                        ),
                ct
            );

            return rows.Count > 0 && rows[^1] is { } outcome
                ? outcome
                : throw new InvalidOperationException("raise_job_alert returned no occurrence_count.");
        }
        catch (DbException ex) when (ex.Message.Contains("ACTA:ALERT_UNKNOWN_JOB:", StringComparison.Ordinal))
        {
            throw new ArgumentException("The referenced job id does not exist.", "jobId", ex);
        }
    }

    public Task<IReadOnlyList<AlertableEvent>> GetAlertableEventsAsync(
        short namespaceId,
        long cursorEventId,
        int batchSize,
        CancellationToken ct
    ) =>
        session.QueryAsync<IReadOnlyList<AlertableEvent>>(
            "Sql/Alerting/GetAlertableEvents.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.NamespaceId, namespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorEventId, cursorEventId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertBatchSize, batchSize));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<AlertableEvent>();
                var rows = new List<AlertableEvent>();
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader));
                }

                return rows;
            },
            ct
        );

    public Task<IReadOnlyList<DeliverableAlert>> GetDeliverableAlertsAsync(
        short namespaceId,
        int batchSize,
        TimeSpan reminderInterval,
        CancellationToken ct
    ) =>
        session.QueryAsync<IReadOnlyList<DeliverableAlert>>(
            "Sql/Alerting/GetDeliverableAlerts.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.NamespaceId, namespaceId));
                // Whole seconds: the option is validated positive, and the reminder arm compares against a
                // day-scale spacing where sub-second precision would mean nothing. Clamped at both ends so
                // a sub-second interval still means "one second" rather than "immediately, every tick",
                // and a decade-long one cannot overflow the INT the three dialects take.
                cmd.Parameters.Add(
                    dialect.CreateParameter(
                        ActaSchema.Sql.AlertReminderSeconds,
                        (int)Math.Clamp(reminderInterval.TotalSeconds, 1d, int.MaxValue)
                    )
                );
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertBatchSize, batchSize));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<DeliverableAlert>();
                var rows = new List<DeliverableAlert>();
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader));
                }

                return rows;
            },
            ct
        );

    // Inline UPDATE in every provider (no routine), so it loads by literal path with no write
    // transaction, matching the provider stores' ExecuteNonQuery-without-transaction shape. The CAS
    // result is read as a returned row (RETURNING / OUTPUT, as extend_worker_leases already does)
    // rather than from rows-affected, which is a driver-dependent number across the three providers.
    public Task<bool> UpdateAlertDeliveryAsync(
        long alertId,
        int expectedVersion,
        AlertDeliveryStatusCode status,
        byte retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    ) =>
        session.QueryAsync(
            "Sql/Alerting/UpdateAlertDelivery.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.Id, alertId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.Version, expectedVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.DeliveryStatusCode, (short)status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.RetryCount, retryCount));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.RetryAfterUtc, retryAfterUtc));
            },
            async (reader, token) => await reader.ReadAsync(token),
            ct
        );

    // Inline UPDATE in every provider (no routine); the number of rows closed is the command's
    // rows-affected count, read after draining the reader.
    public Task<int> ResolveJobAlertsAsync(short namespaceId, long jobId, long sourceEventId, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Alerting/ResolveJobAlerts.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.NamespaceId, namespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SourceEventId, sourceEventId));
            },
            async (reader, token) =>
            {
                while (await reader.NextResultAsync(token)) { }

                return reader.RecordsAffected;
            },
            ct
        );

    public Task<AlertControlOutcome> AcknowledgeJobAlertAsync(AlertControlCommand command, CancellationToken ct) =>
        ControlAsync("AcknowledgeJobAlert", command, ct);

    public Task<AlertControlOutcome> ResolveJobAlertManualAsync(AlertControlCommand command, CancellationToken ct) =>
        ControlAsync("ResolveJobAlertManual", command, ct);

    public Task<AlertPage> ListJobAlertsAsync(AlertPageRequest request, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Alerting/ListJobAlerts.sql",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, request.JobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.JobId, request.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.UnresolvedOnlyFlag, request.UnresolvedOnly));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.SeverityCode, request.SeverityAtLeast));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.DeliveryStatusCode, request.DeliveryStatus));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AcknowledgedFilter, request.Acknowledged));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, request.TagFiltersJson));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCreatedAtUtc, request.CursorCreatedAtUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, request.CursorId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, request.Take));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, request.IncludeTotal ? true : (bool?)null));
            },
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<JobAlertListProjectionRow>();
                var rows = new List<AlertListItem>(request.Take);
                while (await reader.ReadAsync(token))
                {
                    rows.Add(read(reader).ToItem());
                }

                long? total = null;
                if (await reader.NextResultAsync(token) && await reader.ReadAsync(token) && !reader.IsDBNull(0))
                {
                    total = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                }

                return new AlertPage(rows, total);
            },
            ct
        );

    public Task<AlertListItem?> GetJobAlertAsync(Guid alertRef, CancellationToken ct) =>
        session.QueryAsync(
            "Sql/Alerting/GetJobAlert.sql",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.AlertRef, alertRef)),
            async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<JobAlertListProjectionRow>();
                return await reader.ReadAsync(token) ? read(reader).ToItem() : null;
            },
            ct
        );

    private void AddRaiseParameters(DbCommand cmd, RaiseJobAlertCommand command)
    {
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, command.JobNamespace));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.JobId, command.JobId));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.OriginCode, (short)command.Origin));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.SeverityCode, (short)command.Severity));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.KindCode, (short)command.Kind));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.Title, command.Title));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.Message, command.Message));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.ChannelName, command.ChannelName));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.DeliveryStatusCode, (short)command.DeliveryStatus));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.DedupeKey, command.DeduplicationKey));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SourceEventId, command.SourceEventId));
        cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.AlertRef, command.AlertRef));
    }

    private async Task<AlertControlOutcome> ControlAsync(string operation, AlertControlCommand command, CancellationToken ct) =>
        await session.ExecuteSingleAsync(
            new StoreCommand("Alerting", operation),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.AlertRef, command.AlertRef));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, command.Actor.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, command.Actor.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage));
            },
            DbProjectionResolver.Resolve<AlertControlOutcome>(),
            ct
        )
        ?? throw new InvalidOperationException($"Control command '{operation}' returned no rows; it must return exactly one outcome row.");
}
