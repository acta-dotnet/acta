using Acta;
using Acta.Concepts.WorkerCrashRecovery;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "worker-crash-recovery";
var currentJobFile = Path.Combine(Path.GetTempPath(), "acta-worker-crash-recovery-current-job.txt");
var currentSessionFile = Path.Combine(Path.GetTempPath(), "acta-worker-crash-recovery-current-session.txt");
var mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();

if (mode is not ("worker-a" or "worker-b" or "enqueue" or "inspect"))
{
    PrintCommands();
    return;
}

// Keep provider/configuration switches, but do not pass the bare role to .NET's configuration parser.
var hostArgs = args.Where(a =>
        !string.Equals(a, mode, StringComparison.OrdinalIgnoreCase) && a is not "--brief" and not "--pause" and not "--all-columns"
    )
    .ToArray();
var builder = Host.CreateApplicationBuilder(hostArgs);
var provider = builder.Configuration["Acta:Provider"] ?? Environment.GetEnvironmentVariable("ACTA_LOCAL_PROVIDER") ?? "sqlite";
if (LocalDatabase.IsSqlite(provider))
{
    Console.WriteLine("This lab intentionally requires PostgreSQL or SQL Server so two processes are real peer workers.");
    Console.WriteLine("Set ACTA_LOCAL_PROVIDER=postgres (or sqlserver) and the matching ACTA_TEST_* connection string.");
    PrintCommands();
    return;
}

string sessionId;
if (mode == "worker-a")
{
    sessionId = Guid.CreateVersion7().ToString("N");
    File.WriteAllText(currentSessionFile, sessionId);
}
else if (mode is "worker-b" or "enqueue" or "inspect")
{
    if (!File.Exists(currentSessionFile) || string.IsNullOrWhiteSpace(sessionId = File.ReadAllText(currentSessionFile).Trim()))
    {
        Console.WriteLine("No current crash-recovery session exists. Start worker-a first.");
        return;
    }
}
else
{
    sessionId = "none";
}

var lab = new ConceptLab(builder.Configuration, args);
builder.Services.AddSingleton(new WorkerRole(mode, sessionId));
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration, applyMigrations: mode == "worker-a");
    if (mode is "worker-a" or "worker-b")
    {
        j.Run<WorkerCrashRecoveryJobs>(jobNamespace);
        j.ConfigureOptions(o =>
        {
            o.LeaseTtlSeconds = 4;
            o.HeartbeatInterval = TimeSpan.FromSeconds(1);
            o.WorkerDeadAfter = TimeSpan.FromSeconds(5);
            o.DeploymentVersion = $"{mode}:{sessionId[..8]}";
        });
    }
    else
    {
        j.Reference<WorkerCrashRecoveryJobs>(jobNamespace);
    }
});

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();
var operations = host.Services.GetRequiredService<IActaOperations>();

if (mode == "enqueue")
{
    var enqueued = await jobs.EnqueueAsync(
        new CrashRecoveryProbe($"probe-{sessionId}", sessionId),
        o => o.DeduplicationKey(DeduplicationKey.ForDefinition("crash-recovery-probe", $"crash-recovery-probe-{sessionId}"))
    );
    File.WriteAllText(currentJobFile, enqueued.JobRef.ToString());
    Console.WriteLine($"Enqueued {enqueued.JobRef}. Worker A should claim it and terminate at the marked failure point.");
    await host.StopAsync();
    return;
}

if (mode == "worker-a")
{
    Console.WriteLine($"Worker A is ready for session {sessionId[..8]}. In another terminal run enqueue; this process will fail fast.");
    await host.WaitForShutdownAsync();
    return;
}

if (!File.Exists(currentJobFile) || !JobRef.TryParse(File.ReadAllText(currentJobFile).Trim(), out var currentJobRef))
{
    Console.WriteLine("No current probe marker exists. Start worker-a and run enqueue first.");
    await host.StopAsync();
    return;
}
var probe = await jobs.GetAsync(JobLookup.ByRef(currentJobRef));
if (probe is null)
{
    Console.WriteLine($"The marker names {currentJobRef}, but that job is not in this database. Use the same provider for every command.");
    await host.StopAsync();
    return;
}

if (mode == "inspect")
{
    await ShowStateAsync(lab, probe.JobId, sessionId);
    await host.StopAsync();
    return;
}

await ShowStateAsync(lab, probe.JobId, sessionId);
Console.WriteLine("Worker B is live. Waiting for A's short lab lease to expire, then triggering leaderless sys.recovery...");
await Task.Delay(TimeSpan.FromSeconds(6));
var recovery = new JobScheduleLookup(JobLookup.ByDeduplicationKey(jobNamespace, "sys.recovery"), "default");
await operations.Schedules.TriggerNowAsync(recovery, note: "worker crash lab");

using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while ((await jobs.GetAsync(JobLookup.ById(probe.JobId), recoveryTimeout.Token))?.Status.IsTerminal != true)
{
    await Task.Delay(100, recoveryTimeout.Token);
}
await ShowStateAsync(lab, probe.JobId, sessionId);
await host.StopAsync();

static async Task ShowStateAsync(ConceptLab lab, long jobId, string sessionId)
{
    await lab.ShowAllAsync(
        "Explore the complete recoverable job record",
        """
        SELECT *
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "The database is the shared authority for ownership and recovery",
        """
        SELECT job_ref, status, execution_number, failure_count, leased_by_worker_id, leased_by_worker_host, lease_expires_at_utc
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Worker heartbeats are ordinary inspectable rows",
        """
        SELECT worker_id, status, deployment_version, host, process_id, last_seen_at_utc
        FROM workers_view
        WHERE namespace = @jobNamespace AND deployment_version IN (@workerADeployment, @workerBDeployment)
        ORDER BY worker_id
        """,
        new
        {
            jobNamespace = "worker-crash-recovery",
            workerADeployment = $"worker-a:{sessionId[..8]}",
            workerBDeployment = $"worker-b:{sessionId[..8]}",
        }
    );
    await lab.ShowAsync(
        "The event ledger retains claim loss and replay",
        """
        SELECT event, from_status, to_status, execution_number, reason, worker_id
        FROM events_view
        WHERE job_id = @jobId
        ORDER BY event_id
        """,
        new { jobId }
    );
}

static void PrintCommands()
{
    Console.WriteLine("Run in this order against one PostgreSQL or SQL Server database:");
    Console.WriteLine("  dotnet run --project concepts/700-topology-and-deployment/705-worker-crash-recovery -- worker-a");
    Console.WriteLine("  dotnet run --project concepts/700-topology-and-deployment/705-worker-crash-recovery -- enqueue");
    Console.WriteLine("  dotnet run --project concepts/700-topology-and-deployment/705-worker-crash-recovery -- inspect");
    Console.WriteLine("  dotnet run --project concepts/700-topology-and-deployment/705-worker-crash-recovery -- worker-b");
}

namespace Acta.Concepts.WorkerCrashRecovery
{
    public sealed record WorkerRole(string Name, string SessionId);

    public sealed record CrashRecoveryProbe(string ProbeId, string? SessionId = null);

    public sealed class CrashRecoveryJob(WorkerRole role)
    {
        [Job("crash-recovery-probe", MaxAttempts = 3, Backoff = "0s")]
        public async Task Handle(CrashRecoveryProbe input, CancellationToken ct)
        {
            Console.WriteLine($"[{role.Name}] executing {input.ProbeId}; ordinary handler code is at-least-once.");
            if (role.Name == "worker-a" && input.SessionId == role.SessionId)
            {
                Console.WriteLine("[worker-a] worst moment reached: side effect may have happened; process disappears before completion.");
                Console.Out.Flush();
                Environment.FailFast("Intentional worker-a crash for the SQL recovery lab.");
            }

            if (role.Name == "worker-a")
            {
                var staleSession = input.SessionId is { Length: >= 8 } ? input.SessionId[..8] : "legacy";
                Console.WriteLine($"[worker-a] stale probe from session {staleSession} completed without crashing the current session.");
                return;
            }

            await Task.Delay(100, ct);
            Console.WriteLine("[worker-b] replay completed; production code would need idempotency or durable steps.");
        }
    }
}
