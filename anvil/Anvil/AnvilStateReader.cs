using Acta;
using Microsoft.Extensions.Options;

namespace Anvil;

/// <summary>Builds the single state snapshot polled by the Anvil cockpit.</summary>
public sealed class AnvilStateReader(
    IActaOperations operations,
    WorkerProcessLauncher launcher,
    AnvilSession session,
    RateTelemetry telemetry,
    SeedProgress seedProgress,
    IOptions<JobsOptions> options,
    FaultInjectors faults,
    AnvilOutboxDatabase outboxDb,
    RunCounts runCounts
)
{
    private readonly DateTime _processStartUtc = session.ProcessStartUtc;
    private readonly string _namespaceName = session.NamespaceName;
    private readonly TimeSpan _workerDeadAfter = options.Value.WorkerDeadAfter;
    private readonly int _heartbeatSeconds = (int)options.Value.HeartbeatInterval.TotalSeconds;
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;

    public async ValueTask<AnvilState> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            return await ReadFromDbAsync(ct);
        }
        catch (Exception ex)
        {
            return Degraded(DbReason(ex));
        }
    }

    private async ValueTask<AnvilState> ReadFromDbAsync(CancellationToken ct)
    {
        var runCountsTask = runCounts.ReadAsync();
        var workerRowsTask = operations.Workers.ListAsync(new ListWorkersQuery(_namespaceName, PageSize: 50), ct).AsTask();
        var eventsTask = operations.Ledger.ListEventsAsync(new ListEventsQuery(JobNamespace: _namespaceName, PageSize: 100), ct).AsTask();
        await Task.WhenAll(runCountsTask, workerRowsTask, eventsTask);

        var run = await runCountsTask;
        var workerRows = await workerRowsTask;
        var events = await eventsTask;
        var now = DateTime.UtcNow;
        var workers = MergeWorkers(launcher.Snapshot(), workerRows.Items, now);
        var workerNames = workerRows.Items.ToDictionary(row => row.WorkerId, row => row.DeploymentVersion);

        var counts = new AnvilCounts(
            Total: run.Total,
            Ready: run.Ready,
            Executing: run.Executing,
            Done: run.Succeeded,
            Failed: run.Failed,
            ExpectedFailed: session.ExpectedFailures
        );

        telemetry.Record(now, run.Succeeded, run.Ready, run.Executing);

        return new AnvilState(
            NamespaceName: _namespaceName,
            Schema: session.Schema,
            Provider: session.Provider,
            ExecutorsPerWorker: new JobsOptions().MaxConcurrentExecutors,
            Ready: workerRows.Items.Count > 0,
            Counts: counts,
            Beats: new AnvilBeats(_heartbeatSeconds, _leaseTtlSeconds, (int)_workerDeadAfter.TotalSeconds),
            WorkerSummary: SummarizeWorkers(workers),
            Workers: workers,
            RecentEvents: MapEvents(events.Items, workerNames),
            Telemetry: telemetry.Snapshot(),
            Seeding: seedProgress.Snapshot(),
            Faults: faults.Snapshot(),
            Outbox: await ReadOutboxAsync(ct),
            DbError: null
        );
    }

    // Guarded separately: a broken producer file degrades only this line, never the whole state read.
    private async Task<AnvilOutboxSnapshot?> ReadOutboxAsync(CancellationToken ct)
    {
        try
        {
            var (pending, quarantined) = await outboxDb.CountsAsync(ct);
            return new AnvilOutboxSnapshot(pending, quarantined);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private AnvilState Degraded(string reason)
    {
        var workers = MergeWorkers(launcher.Snapshot(), [], DateTime.UtcNow, databaseAvailable: false);
        return new AnvilState(
            NamespaceName: _namespaceName,
            Schema: session.Schema,
            Provider: session.Provider,
            ExecutorsPerWorker: new JobsOptions().MaxConcurrentExecutors,
            Ready: false,
            Counts: null,
            Beats: new AnvilBeats(_heartbeatSeconds, _leaseTtlSeconds, (int)_workerDeadAfter.TotalSeconds),
            WorkerSummary: SummarizeWorkers(workers),
            Workers: workers,
            RecentEvents: [],
            Telemetry: telemetry.Snapshot(),
            Seeding: seedProgress.Snapshot(),
            Faults: faults.Snapshot(),
            Outbox: null,
            DbError: reason
        );
    }

    private IReadOnlyList<AnvilWorker> MergeWorkers(
        IReadOnlyList<WorkerSnapshot> managedWorkers,
        IReadOnlyList<ListWorkersItem> rows,
        DateTime utcNow,
        bool databaseAvailable = true
    )
    {
        var shown = new HashSet<int>();
        var result = new List<AnvilWorker>();

        foreach (var managed in managedWorkers)
        {
            var row = NewestOwnRow(rows, managed.Name);
            if (row is not null)
            {
                shown.Add(row.WorkerId);
            }
            result.Add(InterpretWorker(managed, row, utcNow, _workerDeadAfter, databaseAvailable));
        }

        foreach (var row in rows)
        {
            if (shown.Contains(row.WorkerId) || row.Status is not (WorkerStatusCode.Active or WorkerStatusCode.Draining))
            {
                continue;
            }
            result.Add(InterpretWorker(null, row, utcNow, _workerDeadAfter, databaseAvailable));
        }

        return result;
    }

    private ListWorkersItem? NewestOwnRow(IReadOnlyList<ListWorkersItem> rows, string deploymentVersion)
    {
        ListWorkersItem? newest = null;
        foreach (var row in rows)
        {
            if (
                row.DeploymentVersion == deploymentVersion
                && row.StartedAtUtc >= _processStartUtc
                && (newest is null || row.StartedAtUtc > newest.StartedAtUtc)
            )
            {
                newest = row;
            }
        }
        return newest;
    }

    private static AnvilWorker InterpretWorker(
        WorkerSnapshot? process,
        ListWorkersItem? row,
        DateTime utcNow,
        TimeSpan workerDeadAfter,
        bool databaseAvailable
    )
    {
        if (process is null)
        {
            return new AnvilWorker(
                Id: null,
                Name: row!.DeploymentVersion,
                Pid: row.ProcessId,
                DisplayState: "external",
                DisplayTitle: "EXTERNAL",
                DisplayMessage: "This database worker was not started by the current Anvil process.",
                ProcessStatus: "external",
                DatabaseStatus: row.Status.ToString().ToLowerInvariant(),
                LastSeenAtUtc: row.LastSeenAtUtc,
                ProcessExitedAtUtc: null,
                ExitCode: null,
                ApproximateRecoveryRemainingSeconds: null,
                CanCrash: false,
                CanDrain: false,
                Managed: false,
                LastErrorLine: null
            );
        }

        var running = process.ProcessStatus == "running";
        var dbStatus = row?.Status;
        var unexpectedExit = !running && !process.DrainRequested;
        var displayState = "stopped";
        var title = "STOPPED";
        var message = "The worker process has stopped.";
        int? recoveryRemaining = null;

        if (!running && process.DrainRequested && dbStatus is not (WorkerStatusCode.Active or WorkerStatusCode.Draining))
        {
            message = "The worker completed a graceful drain and stopped.";
        }
        else if (process.DrainRequested || dbStatus == WorkerStatusCode.Draining)
        {
            displayState = "draining";
            title = "DRAINING";
            message = "The worker is completing its current work and will not claim additional jobs.";
        }
        else if (running && dbStatus == WorkerStatusCode.Active)
        {
            displayState = "healthy";
            title = "HEALTHY";
            message = "Process running · Database active";
        }
        else if (running)
        {
            displayState = "starting";
            title = "STARTING";
            message = "Process started. Waiting for Acta registration and first heartbeat.";
        }
        else if (unexpectedExit && dbStatus is WorkerStatusCode.Active or WorkerStatusCode.Draining)
        {
            displayState = "crashed";
            title = "CRASHED";
            message = "The process has stopped, but Acta is still honoring its current leases.";
            var expectedRecoveryAt = row!.LastSeenAtUtc + workerDeadAfter;
            recoveryRemaining = Math.Max(0, (int)Math.Ceiling((expectedRecoveryAt - utcNow).TotalSeconds));
        }
        else if (unexpectedExit && dbStatus == WorkerStatusCode.Dead)
        {
            displayState = "recovered";
            title = "RECOVERED";
            message = "Acta marked the worker dead. Its abandoned work is now eligible to be reclaimed.";
        }
        else if (unexpectedExit && row is null && process.CrashRequested)
        {
            displayState = "crashed";
            title = "CRASHED";
            message = databaseAvailable
                ? "The process stopped before Acta registration completed."
                : "The process has stopped. Database recovery status is currently unavailable.";
        }
        else if (unexpectedExit && row is null)
        {
            message = "The process exited before Acta registration completed.";
        }

        return new AnvilWorker(
            Id: process.Id,
            Name: process.Name,
            Pid: process.Pid,
            DisplayState: displayState,
            DisplayTitle: title,
            DisplayMessage: message,
            ProcessStatus: process.ProcessStatus,
            DatabaseStatus: row?.Status.ToString().ToLowerInvariant() ?? (databaseAvailable ? "unregistered" : "unavailable"),
            LastSeenAtUtc: row?.LastSeenAtUtc,
            ProcessExitedAtUtc: process.ExitedAtUtc,
            ExitCode: process.ExitCode,
            ApproximateRecoveryRemainingSeconds: recoveryRemaining,
            CanCrash: process.Id > 0 && displayState is "healthy" or "starting" && !process.CrashRequested,
            CanDrain: process.Id > 0 && displayState == "healthy" && !process.DrainRequested,
            Managed: true,
            LastErrorLine: process.LastErrorLine
        );
    }

    private static AnvilWorkerSummary SummarizeWorkers(IReadOnlyList<AnvilWorker> workers) =>
        new(
            Healthy: workers.Count(worker => worker.DisplayState == "healthy"),
            Starting: workers.Count(worker => worker.DisplayState == "starting"),
            Draining: workers.Count(worker => worker.DisplayState == "draining"),
            AwaitingRecovery: workers.Count(worker => worker.DisplayState == "crashed"),
            Recovered: workers.Count(worker => worker.DisplayState == "recovered"),
            Stopped: workers.Count(worker => worker.DisplayState == "stopped"),
            External: workers.Count(worker => worker.DisplayState == "external")
        );

    private static IReadOnlyList<AnvilEvent> MapEvents(IReadOnlyList<ListEventsItem> rows, IReadOnlyDictionary<int, string> workerNames) =>
        rows.Select(row => new AnvilEvent(
                TimeUtc: row.CreatedAtUtc,
                EventCode: row.EventCode.ToString(),
                WorkerName: row.WorkerId is { } id ? workerNames.GetValueOrDefault(id) ?? $"worker {id}" : null,
                JobRef: row.JobRef?.ToString(),
                FromStatus: null,
                ToStatus: Detail(row.DetailsText, "to"),
                ExecutionStatus: row.EventCode
                    is EventCode.JobExecutionSucceeded
                        or EventCode.JobExecutionFailed
                        or EventCode.JobExecutionOrphaned
                        or EventCode.JobExecutionYielded
                        or EventCode.JobFailed
                    ? row.EventCode.ToString()
                    : null,
                DurationMs: row.DurationMs,
                Reason: row.Message ?? Detail(row.DetailsText, "reason")
            ))
            .ToArray();

    // The named text fact of an event's JSON details text, or null.
    private static string? Detail(string? detailsText, string name)
    {
        if (detailsText is null)
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(detailsText);
            return
                document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string DbReason(Exception ex)
    {
        var message = ex is OperationCanceledException ? "database read timed out: server unreachable or overloaded" : ex.Message;
        var line = message.Split('\n', '\r')[0].Trim();
        return line.Length > 160 ? line[..160] : line;
    }
}

public sealed record AnvilState(
    string NamespaceName,
    string Schema,
    string Provider,
    int ExecutorsPerWorker,
    bool Ready,
    AnvilCounts? Counts,
    AnvilBeats Beats,
    AnvilWorkerSummary WorkerSummary,
    IReadOnlyList<AnvilWorker> Workers,
    IReadOnlyList<AnvilEvent> RecentEvents,
    TelemetrySnapshot Telemetry,
    SeedProgressSnapshot Seeding,
    FaultSnapshot Faults,
    AnvilOutboxSnapshot? Outbox,
    string? DbError
);

/// <summary>Producer-file backlog for the outbox-pressure fault; null when the file is unreadable.</summary>
public sealed record AnvilOutboxSnapshot(long Pending, long Quarantined);

public sealed record AnvilCounts(long Total, long Ready, long Executing, long Done, long Failed, long ExpectedFailed);

public sealed record AnvilBeats(int HeartbeatSeconds, int LeaseTtlSeconds, int DeadAfterSeconds);

public sealed record AnvilWorkerSummary(
    int Healthy,
    int Starting,
    int Draining,
    int AwaitingRecovery,
    int Recovered,
    int Stopped,
    int External
);

public sealed record AnvilWorker(
    int? Id,
    string Name,
    int? Pid,
    string DisplayState,
    string DisplayTitle,
    string DisplayMessage,
    string ProcessStatus,
    string DatabaseStatus,
    DateTime? LastSeenAtUtc,
    DateTime? ProcessExitedAtUtc,
    int? ExitCode,
    int? ApproximateRecoveryRemainingSeconds,
    bool CanCrash,
    bool CanDrain,
    bool Managed,
    string? LastErrorLine
);

public sealed record AnvilEvent(
    DateTime TimeUtc,
    string EventCode,
    string? WorkerName,
    string? JobRef,
    string? FromStatus,
    string? ToStatus,
    string? ExecutionStatus,
    int? DurationMs,
    string? Reason
);
