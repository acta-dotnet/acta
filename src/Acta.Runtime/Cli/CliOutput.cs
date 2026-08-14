using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Acta.Runtime.Cli;

/// <summary>
/// CLI result writers: plain key-value lines for humans, hand-written JSON for scripting. JSON is
/// emitted via Utf8JsonWriter so the core stays reflection-free for native AOT.
/// </summary>
internal static class CliOutput
{
    /// <summary>
    /// Writes a control-verb result in plain or JSON format.
    /// </summary>
    public static void WriteControl(TextWriter writer, string verb, JobControlResult result, bool json)
    {
        if (json)
        {
            WriteJson(
                writer,
                w =>
                {
                    w.WriteString("verb", verb);
                    w.WriteNumber("jobId", result.JobId);
                    w.WriteString("action", result.Action.ToString());
                    if (result.Status is { } status)
                    {
                        w.WriteString("status", status.ToString());
                    }
                    else
                    {
                        w.WriteNull("status");
                    }
                }
            );
            return;
        }

        writer.WriteLine($"job: {result.JobId}");
        writer.WriteLine($"action: {result.Action}");
        writer.WriteLine($"status: {(result.Status is { } s ? s.ToString() : "(none)")}");
    }

    /// <summary>
    /// Writes a job snapshot in plain or JSON format.
    /// </summary>
    public static void WriteSnapshot(TextWriter writer, JobDetail snapshot, bool json)
    {
        if (json)
        {
            WriteJson(
                writer,
                w =>
                {
                    w.WriteString("jobRef", snapshot.JobRef.ToString());
                    w.WriteNumber("jobId", snapshot.JobId);
                    w.WriteString("namespace", snapshot.JobNamespace);
                    w.WriteString("name", snapshot.JobName);
                    if (snapshot.TenantId is { } tenantId)
                    {
                        w.WriteNumber("tenantId", tenantId);
                    }
                    else
                    {
                        w.WriteNull("tenantId");
                    }
                    if (snapshot.DeduplicationKey is { } key)
                    {
                        w.WriteString("deduplicationKey", key);
                    }
                    else
                    {
                        w.WriteNull("deduplicationKey");
                    }
                    w.WriteString("status", snapshot.Status.ToString());
                    w.WriteString("createdUtc", snapshot.CreatedAtUtc.ToString("O"));
                    w.WriteNumber("failureCount", snapshot.FailureCount);
                }
            );
            return;
        }

        writer.WriteLine($"job-ref: {snapshot.JobRef}");
        writer.WriteLine($"job: {snapshot.JobId}");
        writer.WriteLine($"namespace: {snapshot.JobNamespace}");
        writer.WriteLine($"name: {snapshot.JobName}");
        writer.WriteLine($"tenant: {(snapshot.TenantId is { } tenant ? tenant.ToString(CultureInfo.InvariantCulture) : "(none)")}");
        writer.WriteLine($"deduplication-key: {snapshot.DeduplicationKey ?? "(none)"}");
        writer.WriteLine($"status: {snapshot.Status}");
        writer.WriteLine($"created-utc: {snapshot.CreatedAtUtc:O}");
        writer.WriteLine($"failure-count: {snapshot.FailureCount}");

        // The snapshot reports state, not why. On a failed or cancelled job, point the operator at
        // the event timeline, which still carries the reason code and message.
        if (snapshot.Status is JobStatusCode.Failed or JobStatusCode.Cancelled)
        {
            writer.WriteLine($"explain: run 'jobs explain {snapshot.JobRef}' for a plain-English account and next action");
            writer.WriteLine($"events: run 'jobs events {snapshot.JobRef}' for the timeline and failure reason");
        }
    }

    /// <summary>
    /// Writes a job explanation. Plain output is flowing prose: an identity line, the one-sentence
    /// headline, a line per step, the recovery expectation when a lease has lapsed, and a trailing
    /// "Next action" line. JSON emits the structured <see cref="JobExplanation"/> for tooling and the
    /// dashboard.
    /// </summary>
    public static void WriteExplanation(TextWriter writer, JobExplanation x, bool json)
    {
        if (json)
        {
            WriteJson(
                writer,
                w =>
                {
                    w.WriteString("jobRef", x.JobRef.ToString());
                    w.WriteNumber("jobId", x.JobId);
                    w.WriteString("namespace", x.JobNamespace);
                    w.WriteString("name", x.JobName);
                    w.WriteString("status", x.Status.ToString());
                    w.WriteString("statusMeaning", x.StatusMeaning);
                    w.WriteString("headline", x.Headline);

                    if (x.ActiveWait is { } wait)
                    {
                        w.WriteStartObject("activeWait");
                        w.WriteString("kind", wait.Kind.ToString());
                        w.WriteString("name", wait.Name);
                        WriteJsonInstantOrNull(w, "dueAtUtc", wait.DueAtUtc);
                        w.WriteEndObject();
                    }
                    else
                    {
                        w.WriteNull("activeWait");
                    }

                    if (x.Lease is { } lease)
                    {
                        w.WriteStartObject("lease");
                        w.WriteNumber("workerId", lease.WorkerId);
                        WriteJsonStringOrNull(w, "workerName", lease.WorkerName);
                        WriteJsonInstantOrNull(w, "expiresAtUtc", lease.ExpiresAtUtc);
                        w.WriteBoolean("expired", lease.Expired);
                        WriteJsonInstantOrNull(w, "workerLastSeenAtUtc", lease.WorkerLastHeartbeatAtUtc);
                        w.WriteBoolean("workerStale", lease.WorkerStale);
                        w.WriteString("recoveryExpectation", lease.RecoveryExpectation);
                        w.WriteEndObject();
                    }
                    else
                    {
                        w.WriteNull("lease");
                    }

                    w.WriteStartArray("steps");
                    foreach (var s in x.Steps)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", s.Name);
                        w.WriteString("state", s.Status.ToString());
                        w.WriteString("explanation", s.Explanation);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();

                    WriteJsonStringOrNull(w, "lastExecutedBy", x.LastExecutedBy);
                    WriteJsonStringOrNull(w, "reason", x.Reason);

                    w.WriteStartArray("nextActions");
                    foreach (var a in x.NextActions)
                    {
                        w.WriteStartObject();
                        w.WriteString("kind", a.Kind);
                        w.WriteString("description", a.Description);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
            );
            return;
        }

        writer.WriteLine($"{x.JobRef}  {x.JobNamespace}/{x.JobName}");
        writer.WriteLine(x.Headline);

        if (x.Reason is { } reason)
        {
            writer.WriteLine();
            writer.WriteLine("Reason:");
            writer.WriteLine($"- {Sentence(reason)}");
        }

        if (x.Lease is { Expired: true } expiredLease)
        {
            writer.WriteLine();
            writer.WriteLine("Worker:");
            writer.WriteLine($"- {LeaseWorkerLabel(expiredLease)}.");
            if (expiredLease.WorkerLastHeartbeatAtUtc is { } seen)
            {
                writer.WriteLine($"- Last heartbeat at {seen:O}.");
            }
            if (expiredLease.WorkerStale)
            {
                writer.WriteLine("- Worker is marked Dead.");
            }
            writer.WriteLine($"- {Sentence(expiredLease.RecoveryExpectation)}");
        }

        if (x.Lease is null && x.LastExecutedBy is { } lastWorker)
        {
            writer.WriteLine();
            writer.WriteLine("Last activity:");
            writer.WriteLine($"- Last executed on worker {lastWorker}.");
        }

        if (x.Steps.Count > 0 || x.ActiveWait?.Kind == JobCheckpointKindCode.Timer)
        {
            writer.WriteLine();
            writer.WriteLine("Durable work:");
            foreach (var s in x.Steps)
            {
                writer.WriteLine($"- Step \"{s.Name}\" {s.Explanation}.");
            }
            if (x.ActiveWait?.Kind == JobCheckpointKindCode.Timer && x.Lease is null)
            {
                writer.WriteLine("- The job holds no executor while waiting.");
            }
        }

        if (x.NextActions.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine(x.NextActions.Count == 1 ? "Next action:" : "Next actions:");
            foreach (var action in x.NextActions)
            {
                writer.WriteLine($"- {Sentence(action.Description)}");
            }
        }
    }

    private static string LeaseWorkerLabel(JobExplainLease lease)
    {
        var worker = lease.WorkerName is { Length: > 0 } name ? $"Worker {name} ({lease.WorkerId})" : $"Worker {lease.WorkerId}";
        return lease.ExpiresAtUtc is { } expires ? $"{worker}, lease expired at {expires:O}" : worker;
    }

    private static string Sentence(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var text = char.ToUpperInvariant(value[0]) + value[1..];
        return text[^1] is '.' or '!' or '?' ? text : text + ".";
    }

    private static void WriteJsonInstantOrNull(Utf8JsonWriter w, string name, DateTime? value)
    {
        if (value is { } v)
        {
            w.WriteString(name, v.ToString("O"));
        }
        else
        {
            w.WriteNull(name);
        }
    }

    /// <summary>
    /// Writes a page of audit events newest first in plain or JSON format. Each event prints its
    /// instant, code, and status transition on one line; reason code and message, when present,
    /// follow on indented lines. A trailing hint surfaces the cursor for the next page.
    /// </summary>
    public static void WriteEvents(TextWriter writer, long jobId, PagedResult<EventListItem> page, bool json)
    {
        if (json)
        {
            WriteJson(
                writer,
                w =>
                {
                    w.WriteNumber("jobId", jobId);
                    w.WriteStartArray("events");
                    foreach (var e in page.Items)
                    {
                        w.WriteStartObject();
                        w.WriteString("createdUtc", e.CreatedAtUtc.ToString("O"));
                        w.WriteString("event", e.EventCode.Code);
                        WriteJsonStringOrNull(w, "fromStatus", e.FromStatus?.Code);
                        WriteJsonStringOrNull(w, "toStatus", e.ToStatus?.Code);
                        WriteJsonStringOrNull(w, "reasonCode", e.ReasonCode?.Code);
                        WriteJsonStringOrNull(w, "reasonMessage", e.ReasonMessage);
                        if (e.WorkerId is { } wid)
                        {
                            w.WriteNumber("workerId", wid);
                        }
                        if (e.ExecutionNumber is { } en)
                        {
                            w.WriteNumber("executionNumber", en);
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    WriteJsonStringOrNull(w, "nextCursor", page.HasMore ? page.NextCursor : null);
                }
            );
            return;
        }

        writer.WriteLine($"Events for job {jobId} (newest first)");
        writer.WriteLine();
        if (page.Items.Count == 0)
        {
            writer.WriteLine("(no events)");
            return;
        }

        foreach (var e in page.Items)
        {
            var transition =
                e.FromStatus is null && e.ToStatus is null ? "" : $"  {e.FromStatus?.Code ?? "?"} -> {e.ToStatus?.Code ?? "?"}";
            var attempt = e.WorkerId is { } wid ? $"  worker {wid}" : "";
            attempt += e.ExecutionNumber is { } en ? $"  exec {en}" : "";
            writer.WriteLine($"{e.CreatedAtUtc:O}  {e.EventCode.Code}{transition}{attempt}");
            if (e.ReasonCode is { } reason)
            {
                writer.WriteLine($"  reason: {reason.Code}");
            }
            if (e.ReasonMessage is { } message)
            {
                writer.WriteLine($"  message: {message}");
            }
        }

        if (page.HasMore && page.NextCursor is { } cursor)
        {
            writer.WriteLine();
            writer.WriteLine($"more events available; next page: jobs events {jobId} --after {cursor}");
        }
    }

    private static void WriteJsonStringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    /// <summary>
    /// Writes a debug-run verdict: the targeted job, the single-run outcome, and the job's
    /// status after the run (null when the row is gone).
    /// </summary>
    public static void WriteDebugRun(TextWriter writer, long jobId, string run, JobStatusCode? status, bool json)
    {
        if (json)
        {
            WriteJson(
                writer,
                w =>
                {
                    w.WriteNumber("jobId", jobId);
                    w.WriteString("run", run);
                    if (status is { } s)
                    {
                        w.WriteString("status", s.ToString());
                    }
                    else
                    {
                        w.WriteNull("status");
                    }
                }
            );
            return;
        }

        writer.WriteLine($"job: {jobId}");
        writer.WriteLine($"run: {run}");
        writer.WriteLine($"status: {(status is { } p ? p.ToString() : "(none)")}");
    }

    /// <summary>
    /// Writes a job id and status in plain or JSON format.
    /// </summary>
    public static void WriteStatus(TextWriter writer, long jobId, JobStatusCode status, bool json)
    {
        if (json)
        {
            WriteJson(
                writer,
                w =>
                {
                    w.WriteNumber("jobId", jobId);
                    w.WriteString("status", status.ToString());
                }
            );
            return;
        }

        writer.WriteLine($"job: {jobId}");
        writer.WriteLine($"status: {status}");
    }

    /// <summary>
    /// Writes usage help listing all verbs and the registered namespaces.
    /// </summary>
    public static void WriteUsage(TextWriter writer, IReadOnlyList<string> namespaces)
    {
        writer.WriteLine("Usage: <app> jobs <verb> <job-ref|deduplication-key|id> [options]");
        writer.WriteLine();
        writer.WriteLine("Verbs:");
        writer.WriteLine("  info     <job-ref|deduplication-key|id>          print the job row");
        writer.WriteLine("  status   <job-ref|deduplication-key|id>          print the current status");
        writer.WriteLine("  result   <job-ref|deduplication-key|id>          print the latest result payload");
        writer.WriteLine("  cancel   <job-ref|deduplication-key|id>          cancel the job (cascades to children)");
        writer.WriteLine("  pause    <job-ref|deduplication-key|id>          pause a job before it runs");
        writer.WriteLine("  resume   <job-ref|deduplication-key|id>          resume a paused job");
        writer.WriteLine("  restart  <job-ref|deduplication-key|id>          reset any non-executing job to Ready");
        writer.WriteLine("  signal   <job-ref|deduplication-key|id> <name>   raise a signal (add --value for a payload)");
        writer.WriteLine(
            "  debug    <job-ref|deduplication-key|id>          run only this one job in-process (this worker runs nothing else)"
        );
        writer.WriteLine("  events   <job-ref|deduplication-key|id>          print the job's audit timeline newest first");
        writer.WriteLine("  explain  <job-ref|deduplication-key|id>          explain the job's state in plain English + next action");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --reason <msg>   reason message for cancel/pause/resume/restart");
        writer.WriteLine("  --value <json>   JSON payload for signal (omit for a presence-only signal)");
        writer.WriteLine("  --take <n>       events page size (default 50, max 100)");
        writer.WriteLine("  --after <cursor> events continuation cursor from a previous page");
        writer.WriteLine("  --ns <ns>        namespace for deduplication-key lookups (required with several registered)");
        writer.WriteLine("  --break          debug: raise the debugger and stop at the job's handler entry");
        writer.WriteLine("  --json           print the result as JSON");
        writer.WriteLine();
        writer.WriteLine("A job_... target is a job ref; any other non-numeric target is a deduplication key;");
        writer.WriteLine("a bare integer is the internal job id (the advanced/debug path).");
        writer.WriteLine("Omit the target to take it from the clipboard.");
        writer.WriteLine();
        writer.WriteLine($"Registered namespaces: {(namespaces.Count == 0 ? "(none)" : string.Join(", ", namespaces))}");
        writer.WriteLine("Exit codes: 0 applied/found, 1 rejected/failed, 2 not found, 64 usage error.");
    }

    // The UTF-8 JSON bytes are decoded to a string and re-encoded by the TextWriter; the CLI host
    // sets Console.OutputEncoding to UTF-8 so non-ASCII reason messages survive the round trip.
    private static void WriteJson(TextWriter writer, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }
        writer.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
