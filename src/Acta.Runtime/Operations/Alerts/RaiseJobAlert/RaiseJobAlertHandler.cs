using System.Data.Common;
using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="AlertLifecycle.RaiseJobAlertAsync"/>: one routine call answering the post-raise occurrence.</summary>
internal sealed class RaiseJobAlertHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<AlertOccurrence> HandleAsync(RaiseJobAlertCommand command, CancellationToken ct)
    {
        try
        {
            var rows = await session.ExecuteAsync(
                new StoreCommand("RaiseJobAlert"),
                cmd =>
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
                },
                static reader =>
                    reader.IsDBNull(0)
                        ? null
                        : new AlertOccurrence(
                            Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                            reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)
                        ),
                ct
            );

            return rows.Count > 0 && rows[^1] is { } occurrence
                ? occurrence
                : throw new InvalidOperationException("raise_job_alert returned no occurrence_count.");
        }
        catch (DbException ex) when (ex.Message.Contains("ACTA:ALERT_UNKNOWN_JOB:", StringComparison.Ordinal))
        {
            throw new ArgumentException("The referenced job id does not exist.", "jobId", ex);
        }
    }
}
