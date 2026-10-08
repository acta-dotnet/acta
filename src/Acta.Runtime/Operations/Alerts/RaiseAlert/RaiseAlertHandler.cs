using System.Data.Common;
using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="AlertLifecycle.RaiseAlertAsync"/>: one routine call answering the post-raise occurrence.</summary>
internal sealed class RaiseAlertHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<RaiseAlertResponse> HandleAsync(RaiseAlertCommand command, CancellationToken ct)
    {
        try
        {
            var rows = await session.ExecuteAsync(
                new StoreCommand("RaiseAlert"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, command.JobNamespace));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.JobId, command.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.OriginCode, (short)command.Origin));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.SeverityCode, (short)command.Severity));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.KindCode, (short)command.Kind));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.Title, command.Title));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.Message, command.Message));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.ChannelName, command.ChannelName));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.DeliveryStatusCode, (short)command.DeliveryStatus));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.DedupeKey, command.DeduplicationKey));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SourceEventId, command.SourceEventId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.AlertRef, command.AlertRef));
                },
                static reader =>
                    reader.IsDBNull(0)
                        ? null
                        : new RaiseAlertResponse(
                            Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                            reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)
                        ),
                ct
            );

            return rows.Count > 0 && rows[^1] is { } occurrence
                ? occurrence
                : throw new InvalidOperationException("raise_alert returned no occurrence_count.");
        }
        catch (DbException ex) when (ex.Message.Contains("ACTA:ALERT_UNKNOWN_JOB:", StringComparison.Ordinal))
        {
            throw new ArgumentException("The referenced job id does not exist.", "jobId", ex);
        }
    }
}
