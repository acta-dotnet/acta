namespace Acta;

/// <summary>The open alert holding a deduplication key within a namespace, the incident a handler resolves by key.</summary>
internal sealed class GetAlertRefByDeduplicationKeyHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetAlertRefByDeduplicationKeyResponse?> HandleAsync(
        GetAlertRefByDeduplicationKeyQuery query,
        CancellationToken ct
    ) =>
        await session.QueryAsync<GetAlertRefByDeduplicationKeyResponse?>(
            "GetAlertRefByDeduplicationKey",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.NamespaceId, query.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.DeduplicationKey, query.DeduplicationKey));
            },
            static async (reader, token) =>
                await reader.ReadAsync(token) ? new GetAlertRefByDeduplicationKeyResponse(new AlertRef(reader.GetGuid(0))) : null,
            ct
        );
}
