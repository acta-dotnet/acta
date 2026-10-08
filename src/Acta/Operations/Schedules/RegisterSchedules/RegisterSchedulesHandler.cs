namespace Acta;

/// <summary>
/// Registers every definition's recurring slot job and its schedules in one set-based call (ensures each
/// slot job, refreshes input, audit, status, and cursor, upserts schedules, sweeps orphans). Every host re-registers every slot at startup, so this runs against slots another
/// host may be executing: it re-asserts the declaration on an idle slot, overwriting an operator
/// reprioritize, and leaves a Dispatched, Executing, held, or finished slot as it stands.
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
            cmd => dialect.BindRegisterSchedules(cmd, command.Definitions, command.SlotRefs, session.Schema),
            DbProjectionResolver.Resolve<RegisterSchedulesRow>(),
            ct
        );
        return new RegisterSchedulesResponse(slots);
    }
}
