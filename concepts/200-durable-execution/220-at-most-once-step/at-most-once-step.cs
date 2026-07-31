using Acta;
using Acta.Concepts.AtMostOnceStep;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "at-most-once-step";
var currentJobFile = Path.Combine(Path.GetTempPath(), "acta-at-most-once-current-job.txt");

var mode = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();
if (mode is not ("crash" or "recover"))
{
    Console.WriteLine("Run this two-process experiment in order:");
    Console.WriteLine("  dotnet run --project concepts/200-durable-execution/220-at-most-once-step -- crash");
    Console.WriteLine("  dotnet run --project concepts/200-durable-execution/220-at-most-once-step -- recover");
    return;
}

// Keep provider/configuration switches, but do not pass the bare mode to .NET's configuration parser.
var hostArgs = args.Where(a =>
        !string.Equals(a, mode, StringComparison.OrdinalIgnoreCase) && a is not "--brief" and not "--pause" and not "--all-columns"
    )
    .ToArray();
var builder = Host.CreateApplicationBuilder(hostArgs);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<AtMostOnceStepJobs>(jobNamespace);
    j.ConfigureOptions(o =>
    {
        // Lab-only timings: production should keep the documented, generous heartbeat margin.
        o.LeaseTtlSeconds = 4;
        o.HeartbeatInterval = TimeSpan.FromSeconds(1);
        o.WorkerDeadAfter = TimeSpan.FromSeconds(5);
    });
});

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();
var operations = host.Services.GetRequiredService<IActaOperations>();

if (mode == "crash")
{
    var runId = Guid.CreateVersion7().ToString("N");
    var enqueued = await jobs.EnqueueAsync(
        new ChargeCard($"order-{runId}", 125_00),
        o => o.DeduplicationKey(DeduplicationKey.ForDefinition("charge-card", $"charge-card-lab-{runId}")).Delayed(TimeSpan.FromSeconds(2))
    );
    File.WriteAllText(currentJobFile, enqueued.JobRef.ToString());
    Console.WriteLine($"Enqueued {enqueued.JobRef}. The process will terminate inside the at-most-once step.");
    Console.WriteLine("A non-zero exit is expected. Then run the recover command against the same database.");
    await host.WaitForShutdownAsync();
    return;
}

if (!File.Exists(currentJobFile) || !JobRef.TryParse(File.ReadAllText(currentJobFile).Trim(), out var currentJobRef))
{
    Console.WriteLine("No current crashed-job marker exists. Run the crash command first.");
    await host.StopAsync();
    return;
}
var crashed = await jobs.GetAsync(JobLookup.ByRef(currentJobRef));
if (crashed is null)
{
    Console.WriteLine(
        $"The marker names {currentJobRef}, but that job is not in this database. Run crash and recover with the same provider."
    );
    await host.StopAsync();
    return;
}

await lab.ShowAllAsync(
    "Explore the complete job record left by the crashed process",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = crashed.JobId }
);
await lab.ShowAsync(
    "After the process loss: execution ownership remains, and the step outcome is pending",
    """
    SELECT job_ref, status, execution_number, leased_by_worker_id, lease_expires_at_utc
    FROM jobs_view
    WHERE job_id = @jobId
    """,
    new { jobId = crashed.JobId }
);
await lab.ShowAsync(
    "The durable start exists, but no completion was recorded",
    """
    SELECT step_name, state, attempt_number, reason
    FROM steps_view
    WHERE job_id = @jobId
    """,
    new { jobId = crashed.JobId }
);

Console.WriteLine("Waiting for the short lab lease to lapse, then triggering the normal sys.recovery job...");
await Task.Delay(TimeSpan.FromSeconds(6));
var recovery = new JobScheduleLookup(JobLookup.ByDeduplicationKey(jobNamespace, "sys.recovery"), "default");
await operations.Schedules.TriggerNowAsync(recovery, note: "at-most-once lab recovery");

using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (true)
{
    var snapshot = await jobs.GetAsync(JobLookup.ById(crashed.JobId), recoveryTimeout.Token);
    if (snapshot?.Status.IsTerminal == true)
    {
        break;
    }
    await Task.Delay(100, recoveryTimeout.Token);
}

await lab.ShowAsync(
    "On replay Acta refuses a second body invocation",
    """
    SELECT step_name, state, attempt_number, reason, reason_message
    FROM steps_view
    WHERE job_id = @jobId
    """,
    new { jobId = crashed.JobId }
);
await lab.ShowAsync(
    "Recovery and reconciliation remain in the event ledger",
    """
    SELECT event, from_status, to_status, execution_number, reason
    FROM events_view
    WHERE job_id = @jobId
    ORDER BY event_id
    """,
    new { jobId = crashed.JobId }
);

var sideEffectFile = ChargeCardJob.SideEffectFile(crashed.JobRef);
var sideEffects = File.Exists(sideEffectFile) ? File.ReadAllLines(sideEffectFile).Length : 0;
Console.WriteLine($"External side-effect records: {sideEffects} (the charge body was not invoked again).");
await host.StopAsync();

namespace Acta.Concepts.AtMostOnceStep
{
    public sealed record ChargeCard(string OrderId, int AmountCents);

    public static class ChargeCardJob
    {
        public static string SideEffectFile(JobRef jobRef) =>
            Path.Combine(Path.GetTempPath(), $"acta-at-most-once-side-effects-{jobRef}.log");

        [Job("charge-card", MaxAttempts = 3, Backoff = "0s")]
        public static async Task Handle(ChargeCard input, JobContext context, CancellationToken ct)
        {
            try
            {
                await context.RunStepAsync(
                    "charge-card",
                    _ =>
                    {
                        // Simulated external system: this write cannot share Acta's SQL transaction.
                        File.AppendAllText(SideEffectFile(context.JobRef), $"{input.OrderId}:{input.AmountCents}{Environment.NewLine}");
                        Console.WriteLine("SIDE EFFECT COMMITTED. Terminating before Acta can record the step outcome...");
                        Console.Out.Flush();
                        Environment.FailFast("Intentional at-most-once lab crash after the external side effect.");
                        return Task.CompletedTask;
                    },
                    options => options.AtMostOnce(),
                    ct
                );
            }
            catch (StepInterruptedException interrupted)
            {
                var sideEffectFile = SideEffectFile(context.JobRef);
                var matchingCharges = File.Exists(sideEffectFile)
                    ? File.ReadLines(sideEffectFile).Count(line => line.StartsWith(input.OrderId + ":", StringComparison.Ordinal))
                    : 0;
                Console.WriteLine($"{interrupted.StepName} is ambiguous; reconciliation found {matchingCharges} external charge(s).");
                Console.WriteLine("The handler owns this policy: compensate, confirm, alert, or continue.");
            }
        }
    }
}
