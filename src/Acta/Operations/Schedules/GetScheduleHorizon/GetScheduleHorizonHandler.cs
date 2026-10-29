namespace Acta;

/// <summary>
/// <see cref="SchedulesOperations.GetScheduleHorizonAsync"/>: the earliest instant the namespace's engine next has
/// anything to do, one seek on the due index, which is how long the admitter may sleep.
/// </summary>
internal sealed class GetScheduleHorizonHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetScheduleHorizonResponse> HandleAsync(GetScheduleHorizonQuery query, CancellationToken ct)
    {
        var rows = await session.QueryListAsync(
            "GetScheduleHorizon",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceId, query.NamespaceId)),
            DbProjectionResolver.Resolve<GetScheduleHorizonRow>(),
            ct
        );
        return new(rows.Count == 0 ? null : rows[0].NextEvaluationAtUtc);
    }
}
