namespace Acta.Runtime;

/// <summary>
/// <see cref="IJobs.GetLineageMapAsync"/>: resolves the lookup, reads the focus, ancestors, steps, checkpoints,
/// and children in one round trip, and maps them.
/// </summary>
internal sealed class GetJobLineageMapHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobLineageMapResponse?> HandleAsync(GetJobLineageMapQuery query, CancellationToken ct)
    {
        if (await lookup.ResolveAsync(query.Job, ct) is not { } jobId)
        {
            return null;
        }

        // Clamp the child limit and fetch one extra so the map can flag a truncated set from the tail.
        var childLimit = Math.Clamp(query.Options?.ChildLimit ?? 100, 1, 1000);
        var data = await ReadAsync(jobId, childLimit + 1, ct);
        return data is null ? null : Map(data, childLimit);
    }

    /// <summary>The lineage sets by job id with at most <paramref name="childFetchLimit"/> children, or null when no row matches.</summary>
    internal async ValueTask<GetJobLineageMapRows?> ReadAsync(long jobId, int childFetchLimit, CancellationToken ct) =>
        await session.QueryAsync<GetJobLineageMapRows?>(
            "GetJobLineageMap",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ChildFetchLimit, childFetchLimit));
            },
            static async (reader, token) =>
            {
                var readJob = DbProjectionResolver.Resolve<GetJobLineageMapResponse.Node>();
                var readStep = DbProjectionResolver.Resolve<GetJobLineageMapStepRow>();
                var readCheckpoint = DbProjectionResolver.Resolve<GetJobLineageMapCheckpointRow>();
                var readChild = DbProjectionResolver.Resolve<GetJobLineageMapResponse.Child>();

                GetJobLineageMapResponse.Node? focus = null;
                while (await reader.ReadAsync(token))
                {
                    focus = readJob(reader);
                }

                var ancestors = new List<GetJobLineageMapResponse.Node>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        ancestors.Add(readJob(reader));
                    }
                }

                var steps = new List<GetJobLineageMapStepRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        steps.Add(readStep(reader));
                    }
                }

                var checkpoints = new List<GetJobLineageMapCheckpointRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        checkpoints.Add(readCheckpoint(reader));
                    }
                }

                var children = new List<GetJobLineageMapResponse.Child>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        children.Add(readChild(reader));
                    }
                }

                return focus is null ? null : new GetJobLineageMapRows(focus, ancestors, steps, checkpoints, children);
            },
            ct
        );

    /// <summary>
    /// Maps the rows to a <see cref="GetJobLineageMapResponse"/>: the focus job, its ancestor context, its steps, the durable wait
    /// it is blocked on, and its direct children. A pure function over the rows plus the child limit; the read fetches
    /// one child past the limit so the tail row here sets <see cref="GetJobLineageMapResponse.ChildrenHasMore"/> without a second query.
    /// </summary>
    internal static GetJobLineageMapResponse Map(GetJobLineageMapRows data, int childLimit)
    {
        var hasMore = data.Children.Count > childLimit;
        IReadOnlyList<GetJobLineageMapResponse.Child> children = hasMore ? [.. data.Children.Take(childLimit)] : data.Children;

        var steps = new List<GetJobExplanationResponse.StepState>(data.Steps.Count);
        foreach (var s in data.Steps)
        {
            steps.Add(new GetJobExplanationResponse.StepState(s.Name, s.Status, s.Status.Description));
        }

        return new GetJobLineageMapResponse(data.Ancestors, data.Focus, steps, FindWait(data.Checkpoints), children, hasMore);
    }

    /// <summary>
    /// The focused job is blocked on a pending signal, child latch, or timer checkpoint; the
    /// precedence is the explanation's (a job awaits one primitive at a time, and a signal is the
    /// operator-actionable one). Each carries its due, so a map shows a bounded wait's deadline and
    /// leaves an unbounded wait's null; a parent parked on a child is the case a lineage map exists
    /// to show.
    /// </summary>
    private static GetJobExplanationResponse.Wait? FindWait(IReadOnlyList<GetJobLineageMapCheckpointRow> checkpoints)
    {
        foreach (var c in checkpoints)
        {
            if (c.Kind == CheckpointKindCode.Signal && c.Status == CheckpointStatusCode.Pending)
            {
                return new GetJobExplanationResponse.Wait(CheckpointKindCode.Signal, c.Name, c.DueAtUtc);
            }
        }
        foreach (var c in checkpoints)
        {
            if (c.Kind == CheckpointKindCode.ChildLatch && c.Status == CheckpointStatusCode.Pending)
            {
                return new GetJobExplanationResponse.Wait(CheckpointKindCode.ChildLatch, c.Name, c.DueAtUtc);
            }
        }
        foreach (var c in checkpoints)
        {
            if (c.Kind == CheckpointKindCode.Timer && c.Status == CheckpointStatusCode.Pending)
            {
                return new GetJobExplanationResponse.Wait(CheckpointKindCode.Timer, c.Name, c.DueAtUtc);
            }
        }
        return null;
    }
}
