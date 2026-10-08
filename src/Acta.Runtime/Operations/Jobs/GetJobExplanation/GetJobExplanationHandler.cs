using System.Globalization;

namespace Acta.Runtime;

/// <summary>
/// <see cref="IJobs.ExplainAsync"/>: resolves the lookup, reads the header, steps, and checkpoints in one round
/// trip, and narrates them against the database clock, so "lease expired 2m ago" measures against the clock
/// that stamped the rows rather than this process's.
/// </summary>
internal sealed class GetJobExplanationHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect, IActaClock clock)
{
    public async ValueTask<GetJobExplanationResponse?> HandleAsync(GetJobExplanationQuery query, CancellationToken ct)
    {
        if (await lookup.ResolveAsync(query.Job, ct) is not { } jobId || await ReadAsync(jobId, ct) is not { } data)
        {
            return null;
        }

        return Explain(data, await clock.GetUtcNowAsync(ct));
    }

    /// <summary>The explanation header plus its step and checkpoint sets by job id, or null when no row matches.</summary>
    internal async ValueTask<GetJobExplanationRows?> ReadAsync(long jobId, CancellationToken ct) =>
        await session.QueryAsync<GetJobExplanationRows?>(
            "GetJobExplanation",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId)),
            static async (reader, token) =>
            {
                var readHeader = DbProjectionResolver.Resolve<GetJobExplanationHeaderRow>();
                var readStep = DbProjectionResolver.Resolve<GetJobExplanationStepRow>();
                var readCheckpoint = DbProjectionResolver.Resolve<GetJobExplanationCheckpointRow>();

                GetJobExplanationHeaderRow? header = null;
                while (await reader.ReadAsync(token))
                {
                    header = readHeader(reader);
                }

                var steps = new List<GetJobExplanationStepRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        steps.Add(readStep(reader));
                    }
                }

                var checkpoints = new List<GetJobExplanationCheckpointRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        checkpoints.Add(readCheckpoint(reader));
                    }
                }

                return header is null ? null : new GetJobExplanationRows(header, steps, checkpoints);
            },
            ct
        );

    /// <summary>
    /// Narrates the rows as a plain-English <see cref="GetJobExplanationResponse"/>: what the Job is doing, why, and the operator's
    /// next move. A pure function over the rows plus the DB clock instant, so the timing math ("lease expired 2m ago")
    /// reads against the clock that stamped the rows and the branches stay deterministic and unit-testable. Decodes
    /// every status to its <c>[Code]</c> description rather than duplicating prose, and branches on Status in the
    /// order the troubleshooting guide prescribes.
    /// </summary>
    internal static GetJobExplanationResponse Explain(GetJobExplanationRows data, DateTime nowUtc)
    {
        var h = data.Header;
        var status = h.Status;
        var steps = BuildSteps(data.Steps);
        var reason = h.LatestReasonMessage ?? (h.LatestReasonCode?.Description is { Length: > 0 } d ? d : null);

        GetJobExplanationWait? wait = null;
        GetJobExplanationLease? lease = null;
        string headline;
        var actions = new List<GetJobExplanationAction>();

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

            case JobStatusCode.Paused when h.NextRunAtUtc is null:
                // Not held (ScheduleWalker.IsHeld): a recurring job whose schedules offer no run.
                headline = "Paused because none of its schedules has an upcoming run.";
                actions.Add(Move("none", "resume or edit one of its schedules to give it a next run"));
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

            case JobStatusCode.Dispatched:
            case JobStatusCode.Executing:
                lease = BuildLease(h, nowUtc);
                if (lease is { Expired: true })
                {
                    var lapsed = h.LeaseExpiresAtUtc is { } exp ? $"expired {Ago(exp, nowUtc)}" : "has expired";
                    headline = $"{status}, but its lease {lapsed}.";
                    actions.Add(Move("wait-recovery", "wait for sys.recovery to reclaim the job on the next maintenance tick"));
                    actions.Add(Move("cancel", "cancel the job if it should not continue"));
                }
                else
                {
                    var expires = h.LeaseExpiresAtUtc is { } exp ? $" Lease expires in {Humanize(exp - nowUtc)}." : "";
                    headline =
                        status == JobStatusCode.Executing
                            ? $"Executing on {WorkerLabel(h)}.{expires}"
                            : $"Dispatched to {WorkerLabel(h)}; the handler has not started yet.{expires}";
                    actions.Add(Move("none", "no action needed unless the job exceeds its expected runtime"));
                }
                break;

            case JobStatusCode.Succeeded:
                headline = "Succeeded.";
                actions.Add(Move("view-result", "view the result with 'jobs result' if the job stores one"));
                actions.Add(Move("inspect-timeline", "inspect the event timeline if needed"));
                break;

            case JobStatusCode.Failed:
                headline = "Failed.";
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

        return new GetJobExplanationResponse(
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
            reason,
            actions
        );
    }

    /// <summary>A suggested operator move: its stable slug and the prose shown with it.</summary>
    private static GetJobExplanationAction Move(string kind, string description) => new(kind, description);

    /// <summary>
    /// The worker that last ran the job, under the same identity rule as the lease label. Distinct
    /// from Lease: it is set even for states with no live lease (Suspended, Failed), so the
    /// operator sees who last touched it; null once that worker's row is gone.
    /// </summary>
    private static string? LastExecutedByLabel(GetJobExplanationHeaderRow h) =>
        WorkerIdentity(h.LastExecutedByWorkerName, h.LastExecutedByWorkerRef);

    /// <summary>
    /// A Suspended job is blocked on a pending signal, a pending child latch, or a pending timer
    /// checkpoint; a signal wins when several are present (a job awaits one primitive at a time,
    /// and the signal is the operator-actionable one). A signal's and a child latch's due is the
    /// wait's timeout instant, carried through so the headline can name it; a timer's is when it
    /// resumes.
    /// </summary>
    private static GetJobExplanationWait? FindWait(IReadOnlyList<GetJobExplanationCheckpointRow> checkpoints)
    {
        foreach (var c in checkpoints)
        {
            if (c.Kind == CheckpointKindCode.Signal && c.Status == CheckpointStatusCode.Pending)
            {
                return new GetJobExplanationWait(CheckpointKindCode.Signal, c.Name, c.DueAtUtc);
            }
        }
        foreach (var c in checkpoints)
        {
            if (c.Kind == CheckpointKindCode.ChildLatch && c.Status == CheckpointStatusCode.Pending)
            {
                return new GetJobExplanationWait(CheckpointKindCode.ChildLatch, c.Name, c.DueAtUtc);
            }
        }
        foreach (var c in checkpoints)
        {
            if (c.Kind == CheckpointKindCode.Timer && c.Status == CheckpointStatusCode.Pending)
            {
                return new GetJobExplanationWait(CheckpointKindCode.Timer, c.Name, c.DueAtUtc);
            }
        }
        return null;
    }

    /// <summary>
    /// The latch name is the framework-owned sys.child.{id} key, so the readable half is the id: an
    /// operator wants the child's number, not the slot's spelling. A name that does not parse is
    /// shown verbatim rather than guessed at.
    /// </summary>
    private static string ChildLabel(string slotName) =>
        slotName.StartsWith(RaiseChildLatch.NamePrefix, StringComparison.Ordinal)
            ? $"child job {slotName[RaiseChildLatch.NamePrefix.Length..]}"
            : $"child latch \"{slotName}\"";

    private static GetJobExplanationLease? BuildLease(GetJobExplanationHeaderRow h, DateTime nowUtc)
    {
        if (h.LeasedByWorkerId is not { } workerId)
        {
            return null;
        }

        var expired = h.LeaseExpiresAtUtc is { } exp && exp < nowUtc;
        // The recovery job flips a silent worker to Dead once it passes WorkerDeadAfter; that status is
        // the authoritative staleness signal, so no threshold math is needed here.
        var stale = h.WorkerStatus == WorkerStatusCode.Dead;

        string recovery;
        if (expired)
        {
            var budgetExhausted = h.FailureCount + 1 >= h.MaxAttemptsEffective;
            recovery = budgetExhausted
                ? "Recovery should mark it Failed on the next maintenance tick because the retry budget is exhausted."
                : "Recovery should return it to Ready on the next maintenance tick.";
        }
        else
        {
            recovery = "The lease is valid; the worker holds it and heartbeats keep it extended.";
        }

        // The lease is reported whenever runtimes still names a holder, because its timings describe the
        // Job. The ref and the name both come from the LEFT JOIN on that holder, so both go null together
        // once retention purges the workers row - the lease stands, its holder is simply unidentifiable.
        return new GetJobExplanationLease(
            workerId,
            Blank(h.WorkerDeploymentVersion),
            h.LeaseExpiresAtUtc,
            expired,
            h.WorkerLastHeartbeatAtUtc,
            stale,
            recovery,
            h.LeasedByWorkerRef is { } leaseRef ? new WorkerRef(leaseRef) : null
        );
    }

    private static List<GetJobExplanationStep> BuildSteps(IReadOnlyList<GetJobExplanationStepRow> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var list = new List<GetJobExplanationStep>(rows.Count);
        foreach (var s in rows)
        {
            list.Add(new GetJobExplanationStep(s.Name, s.Status, StepPhrase(s)));
        }
        return list;
    }

    private static string StepPhrase(GetJobExplanationStepRow s) =>
        s.Status switch
        {
            StepStatusCode.Succeeded => "succeeded and will not rerun",
            StepStatusCode.Exhausted => s.ReasonMessage is { Length: > 0 } m
                ? $"exhausted after {Attempts(s.AttemptNumber)}: {m}"
                : $"exhausted after {Attempts(s.AttemptNumber)}",
            StepStatusCode.Pending => s.NextRetryAtUtc is not null
                ? s.ReasonMessage is { Length: > 0 } retryReason
                    ? $"is waiting for retry attempt {s.AttemptNumber}: {retryReason}"
                    : $"is waiting for retry attempt {s.AttemptNumber}"
                : "is in progress",
            StepStatusCode.Interrupted =>
                "was interrupted before its outcome was recorded; it may have run 0 or 1 times - reconcile externally",
            _ => s.Status.Description,
        };

    private static string Attempts(int count) => count == 1 ? "1 attempt" : $"{count} attempts";

    /// <summary>
    /// The one rule every worker mention in the prose follows: the deployment version reads as the
    /// worker's name (e.g. "payments-v42"); with no version recorded, its public ref; with the
    /// workers row itself gone (retention purges workers, so a lease or an execution can outlive
    /// its holder), nothing at all. The internal worker id is never prose, and a missing row never
    /// fabricates a ref.
    /// </summary>
    private static string? WorkerIdentity(string? deploymentVersion, Guid? workerRef) =>
        (Blank(deploymentVersion), workerRef) switch
        {
            (string name, Guid) => name,
            (string name, null) => name,
            (null, Guid reference) => new WorkerRef(reference).ToString(),
            (null, null) => null,
        };

    private static string WorkerLabel(GetJobExplanationHeaderRow h) =>
        WorkerIdentity(h.WorkerDeploymentVersion, h.LeasedByWorkerRef) is { } identity ? $"worker {identity}" : "an unknown worker";

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
