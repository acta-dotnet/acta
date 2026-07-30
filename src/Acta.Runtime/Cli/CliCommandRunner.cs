using System.Text;
using Acta.Modules.Execution;
using Acta.Modules.Execution.Workers;
using Acta.Payloads;

namespace Acta.Cli;

/// <summary>
/// Executes one parsed CLI command against the IJobs facade and writes the outcome. Returns the
/// process exit code: 0 applied/found, 1 rejected/failed, 2 not found, 64 usage error.
/// </summary>
internal sealed class CliCommandRunner(
    IJobs jobs,
    IReadOnlyList<WorkerRuntime> runtimes,
    IReadOnlyList<string> namespaces,
    TextWriter output,
    TextWriter error,
    Func<string?>? clipboard = null
)
{
    private const int ExitOk = 0;
    private const int ExitRejected = 1;
    private const int ExitNotFound = 2;
    private const int ExitUsage = 64;

    private static readonly TimeSpan HeartbeatPumpInterval = TimeSpan.FromSeconds(45);

    public async Task<int> RunAsync(CliCommand command, CancellationToken ct)
    {
        if (command.Verb == CliVerb.Help)
        {
            CliOutput.WriteUsage(output, namespaces);
            return ExitOk;
        }

        if (command.Target is null)
        {
            if (!CliClipboard.TryResolveTarget((clipboard ?? CliClipboard.ReadText)(), out var fromClipboard))
            {
                await error.WriteLineAsync("Job id is missing: pass a job id or deduplication-key, or copy one to the clipboard.");
                return ExitUsage;
            }
            command = command with { Target = fromClipboard };
        }

        if (!TryBuildLookup(command, out var lookup, out var usageError))
        {
            await error.WriteLineAsync(usageError);
            return ExitUsage;
        }

        return command.Verb switch
        {
            CliVerb.Info => await InfoAsync(lookup, command.Json, ct),
            CliVerb.Status => await StatusAsync(lookup, command.Json, ct),
            CliVerb.Result => await ResultAsync(lookup, ct),
            CliVerb.Cancel => Control("cancel", await jobs.CancelAsync(lookup, command.Reason, ct: ct), command.Json),
            CliVerb.Pause => Control("pause", await jobs.PauseAsync(lookup, command.Reason, ct: ct), command.Json),
            CliVerb.Resume => Control("resume", await jobs.ResumeAsync(lookup, command.Reason, ct: ct), command.Json),
            CliVerb.Restart => Control("restart", await jobs.RestartAsync(lookup, command.Reason, ct: ct), command.Json),
            CliVerb.Signal => Control(
                "signal",
                await jobs.RaiseSignalAsync(lookup, command.SignalName!, SignalPayload(command.SignalValue), ct: ct),
                command.Json
            ),
            CliVerb.Debug => await DebugAsync(lookup, command.Json, command.Break, ct),
            CliVerb.Events => await EventsAsync(lookup, command.Take, command.Cursor, command.Json, ct),
            CliVerb.Explain => await ExplainAsync(lookup, command.Json, ct),
            _ => ExitUsage,
        };
    }

    private bool TryBuildLookup(CliCommand command, out JobLookup lookup, out string? usageError)
    {
        usageError = null;
        if (JobRef.TryParse(command.Target, out var jobRef))
        {
            lookup = JobLookup.ByRef(jobRef);
            return true;
        }

        if (long.TryParse(command.Target, out var id) && id > 0)
        {
            lookup = JobLookup.ById(id);
            return true;
        }

        var ns = command.Namespace ?? (namespaces.Count == 1 ? namespaces[0] : null);
        if (ns is null)
        {
            lookup = default;
            usageError =
                namespaces.Count == 0
                    ? "Deduplication-key lookup needs --ns; this process registers no namespaces."
                    : $"--ns is required for deduplication-key lookups; registered namespaces: {string.Join(", ", namespaces)}.";
            return false;
        }

        lookup = JobLookup.ByDeduplicationKey(ns, command.Target!);
        return true;
    }

    // A signal value is passed through verbatim as a JSON payload the handler reads via
    // WaitSignalAsync<T>; omitting --value raises a presence-only signal.
    private static JobPayload SignalPayload(string? value) =>
        value is null ? JobPayload.None : JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(value));

    private int Control(string verb, JobControlResult result, bool json)
    {
        CliOutput.WriteControl(output, verb, result, json);
        return result.Action switch
        {
            JobControlAction.Applied => ExitOk,
            JobControlAction.NotFound => ExitNotFound,
            _ => ExitRejected,
        };
    }

    private async Task<int> InfoAsync(JobLookup lookup, bool json, CancellationToken ct)
    {
        var snapshot = await jobs.GetAsync(lookup, ct);
        if (snapshot is null)
        {
            await error.WriteLineAsync("job not found");
            return ExitNotFound;
        }
        CliOutput.WriteSnapshot(output, snapshot, json);
        return ExitOk;
    }

    /// <summary>
    /// Explains the job's durable state in plain English: resolves the target, reads the composite
    /// explain snapshot, and renders the status meaning, active wait, steps, lease/worker liveness, and
    /// recommended next actions. The read-only companion to <c>info</c> (the raw row) and <c>events</c>
    /// (the timeline).
    /// </summary>
    private async Task<int> ExplainAsync(JobLookup lookup, bool json, CancellationToken ct)
    {
        var explanation = await jobs.ExplainAsync(lookup, ct);
        if (explanation is null)
        {
            await error.WriteLineAsync("job not found");
            return ExitNotFound;
        }
        CliOutput.WriteExplanation(output, explanation, json);
        return ExitOk;
    }

    private async Task<int> StatusAsync(JobLookup lookup, bool json, CancellationToken ct)
    {
        var jobId = await jobs.ResolveJobIdAsync(lookup, ct);
        if (jobId is null)
        {
            await error.WriteLineAsync("job not found");
            return ExitNotFound;
        }
        var status = await jobs.GetStatusAsync(JobLookup.ById(jobId.Value), ct);
        if (status is null)
        {
            await error.WriteLineAsync("job not found");
            return ExitNotFound;
        }
        CliOutput.WriteStatus(output, jobId.Value, status.Value, json);
        return ExitOk;
    }

    private async Task<int> ResultAsync(JobLookup lookup, CancellationToken ct)
    {
        var jobId = await jobs.ResolveJobIdAsync(lookup, ct);
        if (jobId is null)
        {
            await error.WriteLineAsync("job not found");
            return ExitNotFound;
        }

        var payload = await jobs.GetResultAsync(JobLookup.ById(jobId.Value), ct);
        if (payload is null || payload.Value.IsNone)
        {
            await output.WriteLineAsync("(no result)");
            return ExitOk;
        }

        var value = payload.Value;
        if (value.Format == JobPayloadFormat.Json || value.Format == JobPayloadFormat.Text)
        {
            await output.WriteLineAsync(Encoding.UTF8.GetString(value.Data.Span));
        }
        else
        {
            await output.WriteLineAsync(Convert.ToBase64String(value.Data.Span));
        }
        return ExitOk;
    }

    /// <summary>
    /// Prints the job's audit timeline newest first: resolves the target to a job id, then reads one
    /// page of ListJobEventsAsync scoped to that id. This is the operator path to the "why" behind a
    /// terminal status, which the job snapshot no longer carries.
    /// </summary>
    private async Task<int> EventsAsync(JobLookup lookup, int? take, string? cursor, bool json, CancellationToken ct)
    {
        var jobId = await jobs.ResolveJobIdAsync(lookup, ct);
        if (jobId is null)
        {
            await error.WriteLineAsync("job not found");
            return ExitNotFound;
        }

        var page = await jobs.ListJobEventsAsync(new ListJobEventsQuery(JobId: jobId, PageSize: take, Cursor: cursor), ct);
        CliOutput.WriteEvents(output, jobId.Value, page, json);
        return ExitOk;
    }

    /// <summary>
    /// Runs an existing job in this process for debugging: resolves the target, initializes the
    /// owning worker's catalog, resets a non-Ready job via restart semantics, then claims exactly
    /// that id and dispatches through the normal runner pipeline. Only this one job runs: the
    /// worker's normal claim loop is never started, so no other jobs are claimed or executed in this
    /// process during the run. A live worker stealing the row between reset and claim surfaces as not
    /// claimable; the race is accepted. The exit code reflects the attempt: a thrown handler counts
    /// as failed even when the retry budget re-arms the job.
    /// </summary>
    private async Task<int> DebugAsync(JobLookup lookup, bool json, bool breakAtHandler, CancellationToken ct)
    {
        var snapshot = await jobs.GetAsync(lookup, ct);
        if (snapshot is null)
        {
            await error.WriteLineAsync("job not found");
            return ExitNotFound;
        }
        var jobId = snapshot.JobId;

        if (snapshot.Status is JobStatusCode.Executing or JobStatusCode.Dispatched)
        {
            await error.WriteLineAsync($"job {jobId} is currently {snapshot.Status}; cannot run it here.");
            return ExitRejected;
        }

        var runtime = runtimes.FirstOrDefault(r => string.Equals(r.WorkerNamespaceName, snapshot.JobNamespace, StringComparison.Ordinal));
        if (runtime is null)
        {
            await error.WriteLineAsync($"no worker for namespace '{snapshot.JobNamespace}' is registered in this process.");
            return ExitRejected;
        }

        await runtime.InitializeAsync(ct);

        try
        {
            var restarted = false;
            if (snapshot.Status != JobStatusCode.Ready)
            {
                var restart = await jobs.RestartAsync(JobLookup.ById(jobId), "cli debug", ct: ct);
                if (restart.Action != JobControlAction.Applied)
                {
                    await error.WriteLineAsync($"could not make job {jobId} Ready: {restart.Action} (status {restart.Status}).");
                    return ExitRejected;
                }
                restarted = true;
            }

            // --break: raise the debugger and stop at the handler seam (see JobRunner). Off Windows
            // there is no JIT attach dialog, so hint that a debugger must be attached manually.
            DebugBreak.Requested = breakAtHandler;
            if (breakAtHandler && !OperatingSystem.IsWindows())
            {
                await error.WriteLineAsync(
                    "--break: no JIT attach dialog off Windows; attach a debugger now and it will break at the handler."
                );
            }

            // A debugger paused at a breakpoint easily outlives the lease TTL; pump the worker
            // heartbeat so the in-flight lease keeps extending while the handler is stopped.
            var runTask = runtime.RunOnceAsync(snapshot.JobNamespace, jobId, ct);
            while (await Task.WhenAny(runTask, Task.Delay(HeartbeatPumpInterval, ct)) != runTask)
            {
                try
                {
                    await runtime.RunHeartbeatOnceAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Lease extension is best effort here; a missed tick only risks a steal, but the
                    // operator watching this run should still see that a tick failed.
                    await error.WriteLineAsync($"debug: heartbeat pump tick failed ({ex.Message}); lease may lapse.");
                }
            }
            var outcome = await runTask;

            if (outcome == RunOnceOutcome.NothingClaimed)
            {
                await error.WriteLineAsync(
                    $"job {jobId} was not claimable (a worker may hold or have taken it, its row was transiently locked, or it is not due)."
                );
                return ExitRejected;
            }

            var baselineFailures = restarted ? (short)0 : snapshot.FailureCount;
            var final = await jobs.GetAsync(JobLookup.ById(jobId), ct);
            var attemptFailed = outcome == RunOnceOutcome.Failed || (final is not null && final.FailureCount > baselineFailures);

            CliOutput.WriteDebugRun(output, jobId, outcome.ToString(), final?.Status, json);
            return attemptFailed ? ExitRejected : ExitOk;
        }
        finally
        {
            DebugBreak.Requested = false;
            await runtime.StopAsync(CancellationToken.None);
        }
    }
}
