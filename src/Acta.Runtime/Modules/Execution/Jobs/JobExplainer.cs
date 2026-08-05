namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// Turns the durable rows read by <c>GetJobExplanationAsync</c> into a plain-English
/// <see cref="JobExplanation"/>: what the Job is doing, why, and the operator's next move. Pure
/// function over the rows plus the DB clock instant (<c>nowUtc</c> is passed in so the
/// timing math ("lease expired 2m ago") reads against the same clock that stamped the rows, and so
/// the branch logic stays deterministic and unit-testable). Decodes every status/state to its
/// <c>[Code]</c> description rather than duplicating prose. Branches on Status in the order the
/// troubleshooting guide (docs/10) prescribes.
/// </summary>
internal static class JobExplainer
{
    public static JobExplanation Explain(JobExplainData data, DateTime nowUtc)
    {
        var h = data.Header;
        var status = h.Status;
        var steps = BuildSteps(data.Steps);
        var reason = h.LatestReasonMessage ?? (h.LatestReasonCode?.Description is { Length: > 0 } d ? d : null);

        JobExplainWait? wait = null;
        JobExplainLease? lease = null;
        string headline;
        var actions = new List<JobExplainAction>();

        switch (status)
        {
            case JobStatusCode.Suspended:
                wait = FindWait(data.Checkpoints);
                switch (wait?.Kind)
                {
                    case JobExplainWaitKind.Signal:
                        headline = $"Suspended, waiting for signal \"{wait.Name}\".";
                        actions.Add(new JobExplainAction("raise-signal", $"raise signal \"{wait.Name}\""));
                        actions.Add(new JobExplainAction("cancel", "cancel the job"));
                        break;
                    case JobExplainWaitKind.Timer:
                        headline = wait.DueAtUtc is { } due
                            ? $"Suspended on durable sleep \"{wait.Name}\", {DuePhrase(due, nowUtc)}."
                            : $"Suspended on durable sleep \"{wait.Name}\".";
                        actions.Add(new JobExplainAction("none", "no action needed - it resumes when the timer is due"));
                        actions.Add(new JobExplainAction("cancel", "cancel the job if the wait is no longer needed"));
                        break;
                    default:
                        headline = "Suspended, but no pending signal or timer checkpoint was found.";
                        actions.Add(new JobExplainAction("inspect-timeline", "inspect the timeline with 'jobs events'"));
                        actions.Add(new JobExplainAction("cancel", "cancel the job"));
                        break;
                }
                break;

            case JobStatusCode.Paused:
                headline = "Paused; it will not run until it is resumed.";
                actions.Add(new JobExplainAction("resume", "resume the job"));
                actions.Add(new JobExplainAction("cancel", "cancel the job"));
                break;

            case JobStatusCode.Ready:
                if (h.NextRunAtUtc is { } next && next > nowUtc)
                {
                    headline = $"Ready, scheduled to run in {Humanize(next - nowUtc)}.";
                    actions.Add(
                        new JobExplainAction("none", "no action needed - the job becomes claimable when its next run time arrives")
                    );
                }
                else
                {
                    headline = "Ready and eligible for claim.";
                    actions.Add(
                        new JobExplainAction("none", $"ensure at least one live worker is running for namespace \"{h.JobNamespace}\"")
                    );
                }
                break;

            case JobStatusCode.Dispatched:
            case JobStatusCode.Executing:
                lease = BuildLease(h, nowUtc);
                if (lease is { Expired: true })
                {
                    var lapsed = h.LeaseExpiresAtUtc is { } exp ? $"expired {Ago(exp, nowUtc)}" : "has expired";
                    headline = $"{status}, but its lease {lapsed}.";
                    actions.Add(
                        new JobExplainAction("wait-recovery", "wait for sys.recovery to reclaim the job on the next maintenance tick")
                    );
                    actions.Add(new JobExplainAction("cancel", "cancel the job if it should not continue"));
                }
                else
                {
                    var expires = h.LeaseExpiresAtUtc is { } exp ? $" Lease expires in {Humanize(exp - nowUtc)}." : "";
                    headline =
                        status == JobStatusCode.Executing
                            ? $"Executing on {WorkerLabel(h)}.{expires}"
                            : $"Dispatched to {WorkerLabel(h)}; the handler has not started yet.{expires}";
                    actions.Add(new JobExplainAction("none", "no action needed unless the job exceeds its expected runtime"));
                }
                break;

            case JobStatusCode.Succeeded:
                headline = "Done.";
                actions.Add(new JobExplainAction("view-result", "view the result with 'jobs result' if the job stores one"));
                actions.Add(new JobExplainAction("inspect-timeline", "inspect the event timeline if needed"));
                break;

            case JobStatusCode.Failed:
                headline = "Failed.";
                actions.Add(new JobExplainAction("inspect-timeline", "inspect the timeline with 'jobs events'"));
                actions.Add(new JobExplainAction("restart", "restart the job only after the underlying cause is fixed"));
                break;

            case JobStatusCode.Cancelled:
                headline = "Cancelled.";
                actions.Add(new JobExplainAction("inspect-timeline", "inspect the timeline with 'jobs events'"));
                actions.Add(new JobExplainAction("restart", "restart the job only if the cancellation should be reversed"));
                break;

            default:
                headline = status.Description;
                break;
        }

        return new JobExplanation(
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

    // The worker that last ran the job, as "name (id)" (or the bare id). Distinct from Lease: it is set
    // even for states with no live lease (Suspended, Failed), so the operator sees who last touched it.
    private static string? LastExecutedByLabel(ExplainHeaderRow h)
    {
        if (h.LastExecutedByWorkerId is not { } id)
        {
            return null;
        }
        var name = Blank(h.LastExecutedByWorkerName);
        return name is not null ? $"{name} ({id})" : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // A Suspended job is blocked on a pending signal or a pending timer checkpoint; a signal wins when
    // both are present (a job awaits one primitive at a time, and the signal is the operator-actionable one).
    private static JobExplainWait? FindWait(IReadOnlyList<ExplainCheckpointRow> checkpoints)
    {
        foreach (var c in checkpoints)
        {
            if (c.Kind == JobCheckpointKindCode.Signal && c.State == JobCheckpointStateCode.Pending)
            {
                return new JobExplainWait(JobExplainWaitKind.Signal, c.Name, null);
            }
        }
        foreach (var c in checkpoints)
        {
            if (c.Kind == JobCheckpointKindCode.Timer && c.State == JobCheckpointStateCode.Pending)
            {
                return new JobExplainWait(JobExplainWaitKind.Timer, c.Name, c.DueAtUtc);
            }
        }
        return null;
    }

    private static JobExplainLease? BuildLease(ExplainHeaderRow h, DateTime nowUtc)
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

        return new JobExplainLease(
            workerId,
            Blank(h.WorkerDeploymentVersion),
            h.LeaseExpiresAtUtc,
            expired,
            h.WorkerLastSeenAtUtc,
            stale,
            recovery
        );
    }

    private static IReadOnlyList<JobExplainStep> BuildSteps(IReadOnlyList<ExplainStepRow> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var list = new List<JobExplainStep>(rows.Count);
        foreach (var s in rows)
        {
            list.Add(new JobExplainStep(s.Name, s.State, StepPhrase(s)));
        }
        return list;
    }

    private static string StepPhrase(ExplainStepRow s) =>
        s.State switch
        {
            JobStepStateCode.Succeeded => "succeeded and will not rerun",
            JobStepStateCode.Exhausted => s.ReasonMessage is { Length: > 0 } m
                ? $"exhausted after {Attempts(s.AttemptNumber)}: {m}"
                : $"exhausted after {Attempts(s.AttemptNumber)}",
            JobStepStateCode.Pending => s.NextRetryAtUtc is not null
                ? s.ReasonMessage is { Length: > 0 } retryReason
                    ? $"is waiting for retry attempt {s.AttemptNumber}: {retryReason}"
                    : $"is waiting for retry attempt {s.AttemptNumber}"
                : "is in progress",
            JobStepStateCode.Interrupted =>
                "was interrupted before its outcome was recorded; it may have run 0 or 1 times - reconcile externally",
            _ => s.State.Description,
        };

    private static string Attempts(short count) => count == 1 ? "1 attempt" : $"{count} attempts";

    // A worker's deployment version reads as its name (e.g. "payments-v42 (17)"); fall back to the bare
    // id when no version was recorded.
    private static string WorkerLabel(ExplainHeaderRow h)
    {
        var name = Blank(h.WorkerDeploymentVersion);
        return (name, h.LeasedByWorkerId) switch
        {
            ({ } n, { } id) => $"worker {n} ({id})",
            (null, { } id) => $"worker {id}",
            _ => "an unknown worker",
        };
    }

    private static string DuePhrase(DateTime due, DateTime now) => due > now ? $"due in {Humanize(due - now)}" : $"due {Ago(due, now)}";

    private static string Ago(DateTime instant, DateTime now) => $"{Humanize(now - instant)} ago";

    // Compact human duration: seconds under a minute, minutes under an hour, then h/m, then d/h.
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
