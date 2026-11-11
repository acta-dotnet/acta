using System.Globalization;

namespace Acta;

/// <summary>
/// <see cref="IJobs.ExplainAsync"/>: resolves the lookup, reads the header, steps, and checkpoints in one statement,
/// and narrates them against the database clock, so "lease expired 2m ago" measures against the clock that stamped the
/// rows rather than this process's.
/// </summary>
internal sealed class ExplainJobHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect, IActaClock clock)
{
    /// <summary>The newest steps an explanation narrates; <see cref="IJobs.ListStepsAsync"/> pages every step.</summary>
    internal const int StepLimit = 50;

    public async ValueTask<ExplainJobResponse?> HandleAsync(ExplainJobQuery query, CancellationToken ct)
    {
        if (
            await getJobId.HandleAsync(new GetJobIdQuery(query.Job), ct) is not { JobId: var jobId }
            || await ReadAsync(jobId, ct) is not { } data
        )
        {
            return null;
        }

        return Explain(data, await clock.GetUtcNowAsync(ct));
    }

    /// <summary>The explanation header plus its step and checkpoint sets by job id, or null when no row matches.</summary>
    internal async ValueTask<ExplainJobRows?> ReadAsync(long jobId, CancellationToken ct) =>
        await session.QueryAsync<ExplainJobRows?>(
            "ExplainJob",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepFetchLimit, StepLimit + 1));
            },
            static async (reader, token) =>
            {
                var readHeader = DbProjectionResolver.Resolve<ExplainJobHeaderRow>();
                var readStep = DbProjectionResolver.Resolve<ExplainJobStepRow>();
                var readCheckpoint = DbProjectionResolver.Resolve<ExplainJobCheckpointRow>();
                ExplainJobHeaderRow? header = null;
                var steps = new List<ExplainJobStepRow>();
                var checkpoints = new List<ExplainJobCheckpointRow>();
                while (await reader.ReadAsync(token))
                {
                    // row_kind leads every row: 1 the header, 2 a step, 3 a checkpoint.
                    switch (Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture))
                    {
                        case 1:
                            header = readHeader(reader);
                            break;
                        case 2:
                            steps.Add(readStep(reader));
                            break;
                        default:
                            checkpoints.Add(readCheckpoint(reader));
                            break;
                    }
                }

                return header is null ? null : new ExplainJobRows(header, steps, checkpoints);
            },
            ct
        );

    /// <summary>
    /// Narrates the rows as a plain-English <see cref="ExplainJobResponse"/>: what the Job is doing, why, and the operator's
    /// next move. A pure function over the rows plus the DB clock instant, so the timing math ("lease expired 2m ago")
    /// reads against the clock that stamped the rows and the branches stay deterministic and unit-testable. Decodes
    /// every status to its <c>[Code]</c> description rather than duplicating prose, and branches on Status in the
    /// order the troubleshooting guide prescribes.
    /// </summary>
    internal static ExplainJobResponse Explain(ExplainJobRows data, DateTime nowUtc)
    {
        var h = data.Header;
        var status = h.Status;
        // Steps arrive in creation order, the newest StepLimit + 1 of them; the oldest drops when there is one too many.
        var stepsHasMore = data.Steps.Count > StepLimit;
        var steps = BuildSteps(stepsHasMore ? [.. data.Steps.Skip(data.Steps.Count - StepLimit)] : data.Steps);
        // A Cancelled job's reason event is its cancel, the only one the read takes for that status.
        var reason = h.Status == JobStatusCode.Cancelled || FailureExplains(h) ? Reason(h) : null;

        ExplainJobWait? wait = null;
        ExplainJobLease? lease = null;
        string headline;
        var actions = new List<ExplainJobAction>();

        switch (status)
        {
            case JobStatusCode.Suspended:
                wait = FindWait(data.Checkpoints);
                switch (wait?.Kind)
                {
                    case CheckpointKindCode.Signal:
                        headline = wait.DueAtUtc is { } expires
                            ? $"Suspended, waiting for signal \"{wait.Name}\"; times out at {Instant(expires)}."
                            : $"Suspended, waiting for signal \"{wait.Name}\".";
                        actions.Add(Move("raise-signal", $"raise signal \"{wait.Name}\""));
                        actions.Add(Move("cancel", "cancel the job"));
                        break;
                    case CheckpointKindCode.ChildLatch:
                    {
                        // No raise-signal action: the sys.child latch is framework-owned and the raise
                        // verb rejects that name, so offering it would suggest a move that cannot work.
                        var child = ChildLabel(wait.Name);
                        headline = wait.DueAtUtc is { } childDue
                            ? $"Suspended, waiting for {child}; times out at {Instant(childDue)}."
                            : $"Suspended, waiting for {child}; no timeout, so it waits until the child completes.";
                        actions.Add(Move("none", $"no action needed - it resumes when {child} reaches a terminal status"));
                        actions.Add(Move("inspect-timeline", $"inspect {child} with 'jobs explain'"));
                        actions.Add(Move("cancel", "cancel the job if the wait is no longer needed"));
                        break;
                    }
                    case CheckpointKindCode.Timer:
                        headline = wait.DueAtUtc is { } due
                            ? $"Suspended on durable sleep \"{wait.Name}\", {DuePhrase(due, nowUtc)}."
                            : $"Suspended on durable sleep \"{wait.Name}\".";
                        actions.Add(Move("none", "no action needed - it resumes when the timer is due"));
                        actions.Add(Move("cancel", "cancel the job if the wait is no longer needed"));
                        break;
                    default:
                        headline = "Suspended, but no pending signal, child, or timer checkpoint was found.";
                        actions.Add(Move("inspect-timeline", "inspect the timeline with 'jobs events'"));
                        actions.Add(Move("cancel", "cancel the job"));
                        break;
                }
                break;

            case JobStatusCode.Idle when h.DefinitionStatus == DefinitionStatusCode.Retired:
                headline = "Idle, and its definition is retired: nothing fires it until a deploy declares the definition again.";
                actions.Add(Move("none", "no action needed - that deploy resumes its schedules from then"));
                break;

            case JobStatusCode.Idle:
                headline = "Idle between firings; its schedules decide the next one.";
                actions.Add(Move("none", "no action needed - the next due schedule admits a firing"));
                actions.Add(Move("cancel", "cancel the job to stop its schedules firing it"));
                break;

            case JobStatusCode.Paused:
                headline = "Paused; it will not run until it is resumed.";
                actions.Add(Move("resume", "resume the job"));
                actions.Add(Move("cancel", "cancel the job"));
                break;

            case JobStatusCode.Ready:
                if (h.NextRunAtUtc is { } next && next > nowUtc)
                {
                    headline = $"Ready, scheduled to run in {Humanize(next - nowUtc)}.";
                    actions.Add(Move("none", "no action needed - the job becomes claimable when its next run time arrives"));
                }
                else
                {
                    headline = "Ready and eligible for claim.";
                    actions.Add(Move("none", $"ensure at least one live worker is running for namespace \"{h.JobNamespace}\""));
                }
                break;

            case JobStatusCode.Executing:
                lease = BuildLease(h, nowUtc);
                if (lease is { Expired: true })
                {
                    var lapsed = h.LeaseExpiresAtUtc is { } exp ? $"expired {Ago(exp, nowUtc)}" : "has expired";
                    headline = $"{status}, but its lease {lapsed}.";
                    actions.Add(Move("wait-recovery", "wait for crash recovery to reclaim the job on its next pass"));
                    actions.Add(Move("cancel", "cancel the job if it should not continue"));
                }
                else
                {
                    var expires = h.LeaseExpiresAtUtc is { } exp ? $" Lease expires in {Humanize(exp - nowUtc)}." : "";
                    headline = $"Executing on {WorkerLabel(h)}.{expires}";
                    actions.Add(Move("none", "no action needed unless the job exceeds its expected runtime"));
                }
                break;

            case JobStatusCode.Succeeded:
                headline = "Succeeded.";
                actions.Add(Move("inspect-timeline", "inspect the event timeline if needed"));
                break;

            case JobStatusCode.Failed:
                // The count reaches the budget only when the retries ran out: a handler's own fail or an exception
                // that is never retried leaves it short, and it then says nothing about how many attempts ran. The
                // budget is the definition's current one, which an override may have changed since, so the line
                // states the count the job ran alone.
                headline =
                    h.Kind == JobKindCode.Ordinary && h.FailureCount > 0 && h.FailureCount >= h.MaxAttemptsEffective
                        ? $"Failed after {h.FailureCount} {(h.FailureCount == 1 ? "attempt" : "attempts")}."
                        : "Failed.";
                actions.Add(Move("inspect-timeline", "inspect the timeline with 'jobs events'"));
                actions.Add(Move("restart", "restart the job only after the underlying cause is fixed"));
                break;

            case JobStatusCode.Cancelled:
                headline = "Cancelled.";
                actions.Add(Move("inspect-timeline", "inspect the timeline with 'jobs events'"));
                actions.Add(Move("restart", "restart the job only if the cancellation should be reversed"));
                break;

            default:
                headline = status.Description;
                break;
        }

        // A recurring job's slot refuses a restart and a trigger while a firing is outstanding; the envelope is the
        // scheduler's own checkpoint, so this is where an operator learns why the verb was rejected. A per-occurrence
        // root carries one too, but restarts, and its schedule triggers again below capacity.
        if (
            h.Kind == JobKindCode.Permanent
            && status != JobStatusCode.Executing
            && data.Checkpoints.Any(c =>
                c.CheckpointKind == CheckpointKindCode.Firing && c.CheckpointStatus == CheckpointStatusCode.Pending
            )
        )
        {
            headline += " A firing is outstanding: restart and trigger now are rejected until it settles.";
        }

        return new ExplainJobResponse(
            h.JobId,
            new JobRef(h.JobRef),
            h.JobNamespace,
            h.JobName,
            status,
            status.Description,
            headline,
            wait,
            lease,
            LastExecutedByLabel(h),
            steps,
            stepsHasMore,
            reason,
            actions
        );
    }

    /// <summary>A suggested operator move: its stable slug and the prose shown with it.</summary>
    private static ExplainJobAction Move(string kind, string description) => new(kind, description);

    /// <summary>
    /// Whether the newest failure is why the job is where it is: it ended a Failed job, and a Ready or Idle job still
    /// counts it, re-armed for a retry or a permanent job whose firing spent its budget. A success, a cancel, a wait, a
    /// pause, a running attempt, or a restart that zeroed the count came after it, so the failure explains none of them.
    /// </summary>
    private static bool FailureExplains(ExplainJobHeaderRow h) =>
        h.Status == JobStatusCode.Failed || (h.Status is JobStatusCode.Ready or JobStatusCode.Idle && h.FailureCount > 0);

    /// <summary>
    /// The reason event's message, else its reason code, followed by its actor, which only a cancel names:
    /// "duplicate order (by alice)", or "Cancelled by alice." for a cancel with no message.
    /// </summary>
    private static string? Reason(ExplainJobHeaderRow h)
    {
        var text = h.LatestReasonMessage ?? EventDetails.Read(h.LatestReasonDetailsFormatId ?? 0, h.LatestReasonDetails, "reason");
        return h.LatestReasonActor is not { } actor ? text
            : text is null ? $"Cancelled by {actor}."
            : $"{text} (by {actor})";
    }

    /// <summary>
    /// The worker that last ran the job, under the same identity rule as the lease label. Distinct
    /// from Lease: it is set even for states with no live lease (Suspended, Failed), so the
    /// operator sees who last touched it; null once that worker's row is gone.
    /// </summary>
    private static string? LastExecutedByLabel(ExplainJobHeaderRow h) =>
        WorkerIdentity(h.LastExecutedByWorkerRef, h.LastExecutedByWorkerVersion);

    /// <summary>
    /// A Suspended job is blocked on a pending signal, a pending child latch, or a pending timer
    /// checkpoint; a signal wins when several are present (a job awaits one primitive at a time,
    /// and the signal is the operator-actionable one). A signal's and a child latch's due is the
    /// wait's timeout instant, carried through so the headline can name it; a timer's is when it
    /// resumes.
    /// </summary>
    private static ExplainJobWait? FindWait(IReadOnlyList<ExplainJobCheckpointRow> checkpoints)
    {
        foreach (var c in checkpoints)
        {
            if (c.CheckpointKind == CheckpointKindCode.Signal && c.CheckpointStatus == CheckpointStatusCode.Pending)
            {
                return new ExplainJobWait(CheckpointKindCode.Signal, c.CheckpointName, c.DueAtUtc);
            }
        }
        foreach (var c in checkpoints)
        {
            if (c.CheckpointKind == CheckpointKindCode.ChildLatch && c.CheckpointStatus == CheckpointStatusCode.Pending)
            {
                return new ExplainJobWait(CheckpointKindCode.ChildLatch, c.CheckpointName, c.DueAtUtc);
            }
        }
        foreach (var c in checkpoints)
        {
            if (c.CheckpointKind == CheckpointKindCode.Timer && c.CheckpointStatus == CheckpointStatusCode.Pending)
            {
                return new ExplainJobWait(CheckpointKindCode.Timer, c.CheckpointName, c.DueAtUtc);
            }
        }
        return null;
    }

    /// <summary>
    /// The latch name is the framework-owned sys.child.{id} key, so the readable half is the id: an
    /// operator wants the child's number, not the latch's spelling. A name that does not parse is
    /// shown verbatim rather than guessed at.
    /// </summary>
    private static string ChildLabel(string latchName) =>
        latchName.StartsWith(RaiseChildLatch.NamePrefix, StringComparison.Ordinal)
            ? $"child job {latchName[RaiseChildLatch.NamePrefix.Length..]}"
            : $"child latch \"{latchName}\"";

    private static ExplainJobLease? BuildLease(ExplainJobHeaderRow h, DateTime nowUtc)
    {
        if (h.LeasedByWorkerId is not { } workerId)
        {
            return null;
        }

        var expired = h.LeaseExpiresAtUtc is { } exp && exp < nowUtc;
        // The recovery pass flips a silent worker to Dead once it passes WorkerDeadAfter; that status is
        // the authoritative staleness signal, so no threshold math is needed here.
        var stale = h.WorkerStatus == WorkerStatusCode.Dead;

        string recovery;
        if (expired)
        {
            var budgetExhausted = h.FailureCount + 1 >= h.MaxAttemptsEffective;
            recovery = (budgetExhausted, h.Kind) switch
            {
                (true, JobKindCode.Permanent) =>
                    "Recovery should return it to Idle on its next pass, with its firing settled Failed, because the retry budget is exhausted.",
                (true, _) => "Recovery should mark it Failed on its next pass because the retry budget is exhausted.",
                _ => "Recovery should return it to Ready on its next pass.",
            };
        }
        else
        {
            recovery = "The lease is valid; the worker holds it and heartbeats keep it extended.";
        }

        // The lease is reported whenever jobs_state still names a holder, because its timings describe the
        // Job. The ref and the version both come from the LEFT JOIN on that holder, so both go null together
        // once retention purges the workers row - the lease stands, its holder is simply unidentifiable.
        return new ExplainJobLease(
            workerId,
            Blank(h.WorkerDeploymentVersion),
            h.LeaseExpiresAtUtc,
            expired,
            h.WorkerLastSeenAtUtc,
            stale,
            recovery,
            h.LeasedByWorkerRef is { } leaseRef ? new WorkerRef(leaseRef) : null
        );
    }

    private static List<ExplainJobStep> BuildSteps(IReadOnlyList<ExplainJobStepRow> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var list = new List<ExplainJobStep>(rows.Count);
        foreach (var s in rows)
        {
            list.Add(new ExplainJobStep(s.StepName, s.StepStatus, StepPhrase(s)));
        }
        return list;
    }

    private static string StepPhrase(ExplainJobStepRow s) =>
        s.StepStatus switch
        {
            StepStatusCode.Succeeded => "succeeded and will not rerun",
            StepStatusCode.Failed => s.FailureMessage is { Length: > 0 } m
                ? $"failed and runs at most once, so it will not rerun: {m}"
                : "failed and runs at most once, so it will not rerun",
            StepStatusCode.Pending => "has started and has no recorded success",
            StepStatusCode.Interrupted =>
                "was interrupted before its outcome was recorded; it may have run 0 or 1 times - reconcile externally",
            _ => s.StepStatus.Description,
        };

    /// <summary>
    /// The one rule every worker mention in the prose follows: the worker's public ref, with its deployment version
    /// beside it when one is recorded ("wrk_... (version 1.4.0)"), since every worker of a deploy shares the version;
    /// with the workers row itself gone (retention purges workers, so a lease or an execution can outlive its holder),
    /// nothing at all. The internal worker id is never prose, and a missing row never fabricates a ref.
    /// </summary>
    private static string? WorkerIdentity(Guid? workerRef, string? deploymentVersion) =>
        workerRef is not { } reference ? null
        : Blank(deploymentVersion) is { } version ? $"{new WorkerRef(reference)} (version {version})"
        : new WorkerRef(reference).ToString();

    private static string WorkerLabel(ExplainJobHeaderRow h) =>
        WorkerIdentity(h.LeasedByWorkerRef, h.WorkerDeploymentVersion) is { } identity ? $"worker {identity}" : "an unknown worker";

    private static string DuePhrase(DateTime due, DateTime now) => due > now ? $"due in {Humanize(due - now)}" : $"due {Ago(due, now)}";

    /// <summary>
    /// An absolute instant, not a relative phrase: a wait's deadline is the thing an operator
    /// diaries against, and it stays true however long the explanation sits in a terminal buffer.
    /// </summary>
    private static string Instant(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Ago(DateTime instant, DateTime now) => $"{Humanize(now - instant)} ago";

    /// <summary>Compact human duration: seconds under a minute, minutes under an hour, then h/m, then d/h.</summary>
    private static string Humanize(TimeSpan span)
    {
        if (span.Ticks < 0)
        {
            span = span.Negate();
        }

        var totalSeconds = (long)span.TotalSeconds;
        if (totalSeconds < 60)
        {
            return $"{totalSeconds}s";
        }

        var totalMinutes = totalSeconds / 60;
        if (totalMinutes < 60)
        {
            return $"{totalMinutes}m";
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hours < 24)
        {
            return minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
        }

        var days = hours / 24;
        var remHours = hours % 24;
        return remHours == 0 ? $"{days}d" : $"{days}d {remHours}h";
    }

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>The header, steps, and checkpoints of one Job, read in one statement.</summary>
internal sealed record ExplainJobRows(
    ExplainJobHeaderRow Header,
    IReadOnlyList<ExplainJobStepRow> Steps,
    IReadOnlyList<ExplainJobCheckpointRow> Checkpoints
);
