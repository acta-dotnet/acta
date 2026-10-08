namespace Acta.Runtime;

/// <summary>
/// The single-statement park admission. It never blocks on the applying tick; the version bump on a
/// supersede is what makes a concurrent consume of the old command miss its CAS.
/// </summary>
internal sealed class ParkOutboxSignalHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<ParkOutboxSignalRow> HandleAsync(ParkOutboxSignalCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("ParkOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.ValueFormatId, command.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Value, command.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StaleBefore, command.StaleBeforeUtc));
            },
            DbProjectionResolver.Resolve<ParkOutboxSignalRow>(),
            ct
        );
}
