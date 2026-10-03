using System.Globalization;

namespace Acta.Runtime;

/// <summary>
/// Spends one turn from a rate meter in one round trip, the only lock operation that writes rows nobody
/// holds: the meter's bucket and the job's reservation.
/// </summary>
internal sealed class ReserveRateHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<RateReservation> HandleAsync(ReserveRateCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("ReserveRate"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.LockKey, command.BucketKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.RateIntervalMilliseconds, command.IntervalMilliseconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.RateBurst, command.Burst));
                // The grace outlasts any lease, so a live job keeps a turn it is coming back for.
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.RateGraceSeconds, command.GraceSeconds));
                // The meter's rows are bookkeeping, never holds, and Guid.Empty is the sentinel that says
                // so. Minted here like every other hold token, so the SQL stays free of per-dialect uuid
                // literals and each provider binds it in its own wire form.
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, Guid.Empty));
            },
            // The routine answers (resume_at_utc, admitted, wait_ms), so the record is built by position.
            static reader => new RateReservation(
                Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.GetDateTimeUtc(0),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)
            ),
            ct
        );
        return rows[0];
    }
}
