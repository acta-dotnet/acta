using System.Globalization;
using Acta;
using Acta.Concepts.WaitSignal;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "wait-signal";
var commandIndex = Array.FindIndex(
    args,
    static arg =>
        arg.Equals("start", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("inspect", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("raise", StringComparison.OrdinalIgnoreCase)
);
var command = commandIndex < 0 ? "run" : args[commandIndex].ToLowerInvariant();
var jobRefIndex = command is "inspect" or "raise" ? commandIndex + 1 : -1;
if (jobRefIndex >= args.Length || (jobRefIndex >= 0 && !JobRef.TryParse(args[jobRefIndex], out _)))
{
    Console.WriteLine($"Usage: dotnet run --project concepts/200-durable-execution/204-wait-signal -- {command} <job-ref>");
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
        j.Reference<WaitSignalJobs>(jobNamespace);
    }
    else
    {
        j.Run<WaitSignalJobs>(jobNamespace);
    }
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

if (command == "inspect")
{
    var inspected = await RequireJobAsync(jobs, args[jobRefIndex]);
    await ShowSignalStateAsync(lab, inspected.JobId);
    await host.StopAsync();
    return;
}

if (command == "raise")
{
    var waiting = await RequireJobAsync(jobs, args[jobRefIndex]);
    await ShowSignalStateAsync(lab, waiting.JobId);
    if (waiting.Status != JobStatusCode.Suspended)
    {
        throw new InvalidOperationException($"Job {waiting.JobRef} is {waiting.Status}, not suspended on the approval signal.");
    }

    await jobs.RaiseSignalAsync(JobLookup.ById(waiting.JobId), "approval", new Decision(true, "alice"));
    Console.WriteLine($"Raised approval for the existing durable identity {waiting.JobRef}.");
    var completed = await WaitForAsync(jobs, waiting.JobId, static snapshot => snapshot.Status.IsTerminal, "signal-waiting job to finish");
    EnsureDone(completed);
    await ShowSignalStateAsync(lab, waiting.JobId);
    await host.StopAsync();
    return;
}

var job = await jobs.EnqueueAsync(new ApproveExpense("exp-1", 4_200m));
var suspended = await WaitForAsync(
    jobs,
    job.JobId,
    static snapshot => snapshot.Status == JobStatusCode.Suspended,
    "job to suspend on approval"
);
await ShowSignalStateAsync(lab, suspended.JobId);

if (command == "start")
{
    Console.WriteLine();
    Console.WriteLine($"Job {suspended.JobRef} is durably suspended. This process will now stop.");
    Console.WriteLine("Inspect and resume that exact identity with:");
    Console.WriteLine($"  dotnet run --project concepts/200-durable-execution/204-wait-signal -- inspect {suspended.JobRef}");
    Console.WriteLine($"  dotnet run --project concepts/200-durable-execution/204-wait-signal -- raise {suspended.JobRef}");
    await host.StopAsync();
    return;
}

if (!lab.Brief && !Console.IsInputRedirected)
{
    Console.WriteLine("Press S to approve the expense...");
    while (Console.ReadKey(intercept: true).Key != ConsoleKey.S) { }
}
else
{
    Console.WriteLine("Brief/non-interactive run: raising the approval signal automatically.");
}

await jobs.RaiseSignalAsync(job, "approval", new Decision(true, "alice"));
Console.WriteLine("approved - the job resumes.");
var terminal = await WaitForAsync(jobs, job.JobId, static snapshot => snapshot.Status.IsTerminal, "signal-waiting job to finish");
EnsureDone(terminal);
await ShowSignalStateAsync(lab, job.JobId);
await host.StopAsync();

static async Task<JobSnapshot> RequireJobAsync(IJobs jobs, string jobRefText)
{
    JobRef.TryParse(jobRefText, out var jobRef);
    return await jobs.GetAsync(JobLookup.ByRef(jobRef))
        ?? throw new InvalidOperationException($"Job {jobRefText} was not found. Use the same configured database for every command.");
}

static async Task<JobSnapshot> WaitForAsync(IJobs jobs, long jobId, Func<JobSnapshot, bool> predicate, string description)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
    if (snapshot.Status != JobStatusCode.Succeeded)
    {
        throw new InvalidOperationException($"Job {snapshot.JobRef} ended {snapshot.Status}; inspect its events for the reason.");
    }
}

static async Task ShowSignalStateAsync(ConceptLab lab, long jobId)
{
    await lab.ShowAllAsync(
        "Explore the complete signal-waiting job record",
        """
        SELECT *
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Current job state and worker ownership",
        """
        SELECT job_ref, status, execution_number, leased_by_worker_id, lease_expires_at_utc
        FROM jobs_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
    await lab.ShowAsync(
        "Current durable approval checkpoint",
        """
        SELECT checkpoint_name, kind, state, value_format
        FROM checkpoints_view
        WHERE job_id = @jobId
        """,
        new { jobId }
    );
}

namespace Acta.Concepts.WaitSignal
{
    public sealed record ApproveExpense(string Id, decimal Amount);

    public sealed record Decision(bool Approved, string By);

    public sealed class ApproveExpenseJob
    {
        [Job("approve-expense")]
        public async Task Handle(ApproveExpense expense, JobContext context, CancellationToken ct)
        {
            // Suspends (no executor held) until the named signal is raised, then re-enters with the latched payload.
            var decision = await context.WaitSignalAsync<Decision>("approval", ct);
            var amount = expense.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            Console.WriteLine($"[{expense.Id}] expense for {amount} EUR {(decision!.Approved ? "APPROVED" : "rejected")} by {decision.By}");
        }
    }
}
