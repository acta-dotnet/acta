namespace Acta;

/// <summary>
/// <see cref="SchedulesOperations.RegisterSchedulesAsync"/>: one call that ensures each permanent definition's slot job
/// and refreshes its input, upserts the schedules, retimes one whose timing changed, sweeps orphans, and stores the
/// namespace's catalog hash.
/// </summary>
internal sealed class RegisterSchedulesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<RegisterSchedulesResponse> HandleAsync(RegisterSchedulesCommand command, CancellationToken ct)
    {
        // Registration never moves a slot: a running one keeps its attempt, an Idle one waits for admission, and only
        // an operator lifts a hold or restarts a finished slot.
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
