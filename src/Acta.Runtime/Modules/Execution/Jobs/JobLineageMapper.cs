namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// Turns the raw rows read by <c>GetJobLineageMapAsync</c> into a <see cref="JobLineageMap"/>: the
/// focus job, its ancestor context, its steps, the durable wait it is blocked on, and its direct
/// children. Pure function over the rows plus the caller's child limit; the read fetches one child past
/// the limit so the tail row here sets <see cref="JobLineageMap.ChildrenHasMore"/> without a second query.
/// </summary>
internal static class JobLineageMapper
{
    public static JobLineageMap Map(JobLineageData data, int childLimit)
    {
        var hasMore = data.Children.Count > childLimit;
        var childRows = hasMore ? data.Children.Take(childLimit) : data.Children;

        var ancestors = new List<JobLineageJob>(data.Ancestors.Count);
        foreach (var a in data.Ancestors)
        {
            ancestors.Add(ToJob(a));
        }

        var steps = new List<JobExplainStep>(data.Steps.Count);
        foreach (var s in data.Steps)
        {
            steps.Add(new JobExplainStep(s.Name, s.Status, s.Status.Description));
        }

        var children = new List<JobLineageChild>();
        foreach (var c in childRows)
        {
            children.Add(new JobLineageChild(c.JobId, new JobRef(c.JobRef), c.JobName, c.Status, c.CreatedAtUtc, c.ModifiedAtUtc));
        }

        return new JobLineageMap(ancestors, ToJob(data.Focus), steps, FindWait(data.Checkpoints), children, hasMore);
    }

    private static JobLineageJob ToJob(LineageJobRow r) =>
        new(
            r.JobId,
            new JobRef(r.JobRef),
            r.JobNamespace,
            r.JobName,
            r.Status,
            r.ParentJobId,
            r.ParentJobRef is { } p ? new JobRef(p) : null,
            r.LineageRootId,
            r.LineageRootJobRef is { } lr ? new JobRef(lr) : null,
            r.CreatedAtUtc,
            r.ModifiedAtUtc
        );

    // The focused job is blocked on a pending signal or a pending timer checkpoint; a signal wins when
    // both are present (a job awaits one primitive at a time, and the signal is the operator-actionable one).
    private static JobExplainWait? FindWait(IReadOnlyList<ExplainCheckpointRow> checkpoints)
    {
        foreach (var c in checkpoints)
        {
            if (c.Kind == JobCheckpointKindCode.Signal && c.Status == JobCheckpointStatusCode.Pending)
            {
                return new JobExplainWait(JobCheckpointKindCode.Signal, c.Name, null);
            }
        }
        foreach (var c in checkpoints)
        {
            if (c.Kind == JobCheckpointKindCode.Timer && c.Status == JobCheckpointStatusCode.Pending)
            {
                return new JobExplainWait(JobCheckpointKindCode.Timer, c.Name, c.DueAtUtc);
            }
        }
        return null;
    }
}
