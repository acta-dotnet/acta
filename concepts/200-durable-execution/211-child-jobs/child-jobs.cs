using Acta;
using Acta.Concepts.ChildJobs;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);
var failChild = args.Contains("--fail-child", StringComparer.OrdinalIgnoreCase);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ChildJobsManifest>("child-jobs");
});
if (failChild)
{
    // The two child failures are the experiment; their durable rows/events are clearer than startup stack traces.
    builder.Logging.AddFilter("Acta", LogLevel.Error);
}

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Children dedupe by name; WaitChildrenAsync resumes from durable latches on replay.
var outcome = await jobs.EnqueueAsync(new BuildSnowman("Frosty", failChild ? "head" : null));
await WaitForAsync(
    jobs,
    outcome.JobId,
    static snapshot => snapshot.Status == JobStatusCode.Suspended,
    "parent to suspend on child latches"
);
await lab.ShowAllAsync(
    "Explore every complete job record in the lineage",
    """
    SELECT *
    FROM jobs_view
    WHERE lineage_root_job_id = @rootJobId
    ORDER BY job_id
    """,
    new { rootJobId = outcome.JobId }
);
await lab.ShowAsync(
    "Parent and children are independent jobs in one lineage",
    """
    SELECT job_id, job_name, status, parent_id, lineage_root_job_id, leased_by_worker_id
    FROM jobs_view
    WHERE lineage_root_job_id = @rootJobId
    ORDER BY job_id
    """,
    new { rootJobId = outcome.JobId }
);
await lab.ShowAsync(
    "The waiting parent owns child-latch checkpoints and no worker",
    """
    SELECT checkpoint_name, kind, state
    FROM checkpoints_view
    WHERE job_id = @parentJobId
    ORDER BY checkpoint_name
    """,
    new { parentJobId = outcome.JobId }
);

var terminal = await WaitForAsync(jobs, outcome.JobId, static snapshot => snapshot.Status.IsTerminal, "parent to reach a terminal state");
if (terminal.Status == JobStatusCode.Done)
{
    var result = await jobs.GetResultAsync<FinishedSnowman>(outcome);
    Console.WriteLine($"snowman finished: {result}");
}
else if (failChild && terminal.Status == JobStatusCode.Failed)
{
    Console.WriteLine("Expected failure mode: the child failed independently and the parent recorded the failed latch outcome.");
}
else
{
    throw new InvalidOperationException($"Parent {terminal.JobRef} ended {terminal.Status}; inspect its events for the reason.");
}
await lab.ShowAsync(
    "Closed latches release the parent for a later execution",
    """
    SELECT job_id, job_name, status, execution_number, parent_id
    FROM jobs_view
    WHERE lineage_root_job_id = @rootJobId
    ORDER BY job_id
    """,
    new { rootJobId = outcome.JobId }
);
await lab.ShowAsync(
    "Every child outcome is now latched on the parent",
    """
    SELECT checkpoint_name, kind, state
    FROM checkpoints_view
    WHERE job_id = @parentJobId
    ORDER BY checkpoint_name
    """,
    new { parentJobId = outcome.JobId }
);
await lab.ShowAsync(
    "Parent and child events retain the independent failure history",
    """
    SELECT job_id, job_name, event, to_status, execution_number, reason
    FROM events_view
    WHERE lineage_root_job_id = @rootJobId
    ORDER BY event_id
    """,
    new { rootJobId = outcome.JobId }
);

await host.StopAsync();

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

namespace Acta.Concepts.ChildJobs
{
    public sealed record BuildSnowman(string Name, string? FailPart);

    public sealed record RollSnowball(string Part, bool ShouldFail);

    public sealed record Snowball(string Part, int Diameter);

    public sealed record FinishedSnowman(string Name, int HeightCm);

    public sealed class SnowmanBuilder
    {
        // StartChildAsync enqueues name-deduped child jobs; WaitChildrenAsync suspends the parent
        // (holding no executor) until every child is terminal; cancelling the parent cascades to children.
        [Job("build-snowman")]
        public async Task<FinishedSnowman> Handle(BuildSnowman input, JobContext context, CancellationToken ct)
        {
            var bottom = await context.StartChildAsync("bottom", new RollSnowball("bottom", input.FailPart == "bottom"), ct: ct);
            var middle = await context.StartChildAsync("middle", new RollSnowball("middle", input.FailPart == "middle"), ct: ct);
            var head = await context.StartChildAsync("head", new RollSnowball("head", input.FailPart == "head"), ct: ct);

            var outcomes = await context.WaitChildrenAsync([bottom.JobId, middle.JobId, head.JobId], ct);
            foreach (var outcome in outcomes)
            {
                if (!outcome.Succeeded)
                {
                    await context.FailAsync($"child {outcome.ChildJobId} ended {outcome.Status}; see its job events for the reason", ct);
                }
            }

            var b = await context.GetChildResultAsync<Snowball>(bottom.JobId, ct);
            var m = await context.GetChildResultAsync<Snowball>(middle.JobId, ct);
            var h = await context.GetChildResultAsync<Snowball>(head.JobId, ct);

            var height = b!.Diameter + m!.Diameter + h!.Diameter;
            Console.WriteLine($"[{input.Name}] stacked {b.Diameter} + {m.Diameter} + {h.Diameter} = {height}cm tall");
            return new FinishedSnowman(input.Name, height);
        }

        [Job("roll-snowball", MaxAttempts = 2, Backoff = "0s")]
        public async Task<Snowball> Roll(RollSnowball input, CancellationToken ct)
        {
            await Task.Delay(input.Part == "bottom" ? 2000 : 1500, ct);
            if (input.ShouldFail)
            {
                throw new InvalidOperationException($"simulated failure while rolling the {input.Part} snowball");
            }
            var diameter = input.Part switch
            {
                "bottom" => 60,
                "middle" => 40,
                _ => 20,
            };
            Console.WriteLine($"  rolled {input.Part} snowball ({diameter}cm)");
            return new Snowball(input.Part, diameter);
        }
    }
}
