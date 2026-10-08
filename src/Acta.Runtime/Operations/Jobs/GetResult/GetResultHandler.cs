namespace Acta.Runtime;

/// <summary><see cref="IJobs.GetResultAsync(JobLookup, CancellationToken)"/>: resolves the lookup, then reads the latest result.</summary>
internal sealed class GetResultHandler(
    JobLookupResolution lookup,
    IDbSession session,
    ISqlDialect dialect,
    IJobPayloadSerializerRegistry serializers
)
{
    public async ValueTask<JobPayload?> HandleAsync(GetResultQuery query, CancellationToken ct)
    {
        if (await lookup.ResolveAsync(query.Job, ct) is not { } jobId)
        {
            return null;
        }

        var record = await ReadAsync(jobId, executionNumber: null, ct);
        return record is null ? null : JobPayload.FromBytes(record.Format, record.Data.ToArray());
    }

    /// <summary>The latest result decoded through its format's serializer, or default when there is none.</summary>
    public async ValueTask<TResult?> HandleAsync<TResult>(GetResultQuery query, CancellationToken ct) =>
        await HandleAsync(query, ct) is { } payload ? serializers.Resolve(payload.Format.Id).Deserialize<TResult>(payload) : default;

    /// <summary>The stored result for the latest execution, or for <paramref name="executionNumber"/>; null when there is none.</summary>
    internal Task<JobResultRow?> ReadAsync(long jobId, int? executionNumber, CancellationToken ct) =>
        session.QuerySingleAsync(
            "GetResult",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Result.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Result.ExecutionNumber, executionNumber));
            },
            DbProjectionResolver.Resolve<JobResultRow>(),
            ct
        );
}
