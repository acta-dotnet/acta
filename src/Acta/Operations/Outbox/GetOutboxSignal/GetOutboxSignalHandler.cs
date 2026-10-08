namespace Acta;

/// <summary>Reads the pending command row under one name, or null when that inbox slot is empty.</summary>
internal sealed class GetOutboxSignalHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetOutboxSignalResponse?> HandleAsync(GetOutboxSignalQuery query, CancellationToken ct) =>
        await session.QuerySingleAsync(
            "GetOutboxSignal",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, query.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Name, query.Name));
            },
            DbProjectionResolver.Resolve<CheckpointValue>(),
            ct
        )
            is { } signal
            ? new GetOutboxSignalResponse(signal)
            : null;
}
