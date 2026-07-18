using Acta;
using Acta.Concepts.DurableSleep;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "durable-sleep";
var commandIndex = Array.FindIndex(
    args,
    static arg =>
        arg.Equals("start", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("inspect", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("recover", StringComparison.OrdinalIgnoreCase)
);
var command = commandIndex < 0 ? "run" : args[commandIndex].ToLowerInvariant();
var jobRefIndex = command is "inspect" or "recover" ? commandIndex + 1 : -1;
if (jobRefIndex >= args.Length || (jobRefIndex >= 0 && !JobRef.TryParse(args[jobRefIndex], out _)))
{
    Console.WriteLine($"Usage: dotnet run --project concepts/200-durable-execution/205-durable-sleep -- {command} <job-ref>");
    return;
}

var hostArgs = args.Where((_, index) => index != commandIndex && index != jobRefIndex).ToArray();
var builder = Host.CreateApplicationBuilder(hostArgs);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    if (command == "inspect")
    {
        j.Reference<DurableSleepJobs>(jobNamespace);
    }
    else
    {
        j.Run<DurableSleepJobs>(jobNamespace);
    }
});

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();

if (command == "inspect")
{
    var inspected = await RequireJobAsync(jobs, args[jobRefIndex]);
    await ShowTimerStateAsync(lab, inspected.JobId);
    await host.StopAsync();
    return;
}

if (command == "recover")
{
    var existing = await RequireJobAsync(jobs, args[jobRefIndex]);
    Console.WriteLine($"Recovering existing durable identity {existing.JobRef}; no new job was enqueued.");
    var recovered = await WaitForAsync(jobs, existing.JobId, static snapshot => snapshot.Status.IsTerminal, "sleeping job to finish");
    EnsureDone(recovered);
    await ShowTimerStateAsync(lab, existing.JobId);
    await host.StopAsync();
    return;
}

var delaySeconds = command == "start" ? 15 : 3;
var job = await jobs.EnqueueAsync(new SendFollowUp("sam@example.com", delaySeconds));
var sleeping = await WaitForAsync(
    jobs,
    job.JobId,
    static snapshot => snapshot.ExecutionNumber >= 1 && snapshot.Status == JobStatusCode.Ready,
    "job to persist its durable timer"
);
await ShowTimerStateAsync(lab, sleeping.JobId);

if (command == "start")
{
    Console.WriteLine();
    Console.WriteLine($"Job {sleeping.JobRef} is sleeping durably. This process will now stop.");
    Console.WriteLine("Inspect and recover that exact identity with:");
    Console.WriteLine($"  dotnet run --project concepts/200-durable-execution/205-durable-sleep -- inspect {sleeping.JobRef}");
    Console.WriteLine($"  dotnet run --project concepts/200-durable-execution/205-durable-sleep -- recover {sleeping.JobRef}");
    await host.StopAsync();
    return;
}

var terminal = await WaitForAsync(jobs, job.JobId, static snapshot => snapshot.Status.IsTerminal, "sleeping job to finish");
EnsureDone(terminal);
await ShowTimerStateAsync(lab, job.JobId);
await host.StopAsync();

static async Task<JobSnapshot> RequireJobAsync(IJobs jobs, string jobRefText)
{
    JobRef.TryParse(jobRefText, out var jobRef);
    return await jobs.GetAsync(JobLookup.ByRef(jobRef))
        ?? throw new InvalidOperationException($"Job {jobRefText} was not found. Use the same configured database for every command.");
}

static async Task<JobSnapshot> WaitForAsync(IJobs jobs, long jobId, Func<JobSnapshot, bool> predicate, string description)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    try
    {
        while (true)
        {
            var snapshot = await jobs.GetAsync(JobLookup.ById(jobId), timeout.Token);
            if (snapshot is not null && predicate(snapshot))
            {
                return snapshot;
            }
            if (snapshot?.Status.IsTerminal == true)
            {
                throw new InvalidOperationException($"Job {snapshot.JobRef} became {snapshot.Status} while waiting for {description}.");
            }
            await Task.Delay(50, timeout.Token);
        }
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        throw new TimeoutException($"Timed out waiting for {description} on job {jobId}.");
    }
}

static void EnsureDone(JobSnapshot snapshot)
{
    if (snapshot.Status != JobStatusCode.Done)
    {
        throw new InvalidOperationException($"Job {snapshot.JobRef} ended {snapshot.Status}; inspect its events for the reason.");
    }
}

static async Task ShowTimerStateAsync(ConceptLab lab, long jobId)
{
    await lab.ShowAllAsync(
        "Explore the complete durable-sleep job record",
        """
        SELECT *
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Current durable timer and worker ownership",
        """
        SELECT job_ref, status, execution_number, next_run_at_utc, leased_by_worker_id, lease_expires_at_utc
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Current durable timer checkpoint",
        """
        SELECT checkpoint_name, kind, state, due_at_utc
        FROM checkpoints_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
}

namespace Acta.Concepts.DurableSleep
{
    public sealed record SendFollowUp(string UserEmail, int CoolDownSeconds);

    public sealed class FollowUpJob
    {
        [Job("send-follow-up")]
        public async Task Handle(SendFollowUp input, JobContext ctx, CancellationToken ct)
        {
            Console.WriteLine($"[{input.UserEmail}] handler entered; preserving the cool-down in a durable timer");

            // Ends this execution and re-arms the job for the database due time. On wake, the handler re-enters here.
            await ctx.SleepAsync("cool-down", TimeSpan.FromSeconds(input.CoolDownSeconds), ct: ct);

            // Durable sleep preserves when work becomes due; it does not make a later external delivery exactly once.
            Console.WriteLine($"[{input.UserEmail}] follow-up is due; delivery still needs its own idempotency policy");
        }
    }
}
