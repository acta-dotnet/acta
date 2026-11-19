namespace Acta;

/// <summary>
/// <see cref="IJobs.GetCheckpointValueAsync"/>: resolves the lookup, then reads one checkpoint's value by its kind and its
/// stored name. Null when no job matches, or the checkpoint does not exist or holds no value.
/// </summary>
internal sealed class GetJobCheckpointValueHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async Task<GetJobCheckpointValueResponse?> HandleAsync(GetJobCheckpointValueQuery query, CancellationToken ct) =>
        await ReadAsync(query.Job, query.Kind, query.Name, ct) is { } row ? new GetJobCheckpointValueResponse(row.Payload) : null;

    /// <summary>The checkpoint's stored value with the time of its write, or null as the public read answers it.</summary>
    internal async ValueTask<GetJobCheckpointValueRow?> ReadAsync(JobLookup job, CheckpointKindCode kind, string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        // A name no checkpoint can be stored under matches nothing, and is never sent: a trailing space or another case
        // would match a stored name under SQL Server's comparison and not under the others'.
        if (!IsStorable(name) || await getJobId.HandleAsync(new GetJobIdQuery(job), ct) is not { JobId: var jobId })
        {
            return null;
        }

        return await session.QueryPayloadAsync(
            dialect,
            "GetJobCheckpointValue",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointKindCode, kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointName, name));
            },
            DbProjectionResolver.Resolve<GetJobCheckpointValueRow>(),
            ct
        );
    }

    // What a checkpoint is stored under: 1 to 128 lowercase letters, digits, dots, and hyphens, a child's sys.child name
    // included.
    private static bool IsStorable(string name) =>
        name.Length is > 0 and <= NameSyntax.ExtendedMaxLength
        && name.All(c => c is '-' or '.' or (>= '0' and <= '9') or (>= 'a' and <= 'z'));
}
