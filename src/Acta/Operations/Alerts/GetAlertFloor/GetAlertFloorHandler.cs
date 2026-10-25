namespace Acta;

/// <summary>
/// <see cref="AlertsOperations.GetAlertFloorAsync"/>: reads the namespace's <c>sys.alerts</c> cursor and skip records in
/// one query and decodes them with the serializer the projector wrote them with.
/// </summary>
internal sealed class GetAlertFloorHandler(IDbSession session, ISqlDialect dialect, IJobPayloadSerializerRegistry serializers)
{
    public async Task<GetAlertFloorResponse> HandleAsync(GetAlertFloorQuery query, CancellationToken ct)
    {
        var rows = await session.QueryListAsync(
            "GetAlertFloor",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceIdFilter, query.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobIdFilter, query.JobId));
            },
            DbProjectionResolver.Resolve<GetAlertFloorRow>(),
            ct
        );

        var cursor = new AlertsCursor(0L, 0L);
        List<PoisonAlertEvent> poison = [];
        foreach (var row in rows)
        {
            if (row.CheckpointName is not { } name || row.ValueFormatId is not { } formatId || row.Value is null)
            {
                continue;
            }

            var serializer = serializers.Resolve(formatId);
            var payload = JobPayload.Trusted(serializer.Format, row.Value);
            if (name == AlertsCursor.VariableName)
            {
                cursor = serializer.Deserialize<AlertsCursor>(payload) ?? cursor;
            }
            else if (PoisonAlertEvent.Parse(name, serializer.Deserialize<string>(payload)) is { } skip)
            {
                poison.Add(skip);
            }
        }

        return new GetAlertFloorResponse(rows.Count > 0, cursor, poison);
    }
}
