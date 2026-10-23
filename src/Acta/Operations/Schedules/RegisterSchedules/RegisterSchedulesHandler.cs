namespace Acta;

/// <summary>
/// <see cref="SchedulesOperations.RegisterSchedulesAsync"/>: one call that ensures each slot job, refreshes input, audit, status, and
/// cursor, upserts schedules, sweeps orphans, and stores the namespace's catalog hash.
/// </summary>
internal sealed class RegisterSchedulesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<RegisterSchedulesResponse> HandleAsync(RegisterSchedulesCommand command, CancellationToken ct)
    {
        // Resetting a running slot would strand its attempt, because the heartbeat cancels any running body
        // missing from the id set it reads back; skipping costs nothing, because the recurring completion
        // takes the next run from schedules rather than from the slot row. Only an operator lifts a hold
        // (ScheduleWalker.IsHeld) or restarts a finished slot.
        var slots = await session.ExecuteAsync(
            new StoreCommand("RegisterSchedules"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.CatalogHash, command.CatalogHash));
                dialect.BindRegisterSchedules(cmd, command.Definitions, command.SlotRefs, session.Schema);
            },
            DbProjectionResolver.Resolve<RegisterSchedulesRow>(),
            ct
        );
        return new RegisterSchedulesResponse(slots);
    }
}
