using Acta;
using Acta.Concepts.ExclusiveKey;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ExclusiveKeyJobs>("exclusive-key");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Shared exclusive key serializes whole jobs: the key is taken after claim, before the handler; a worker claiming the second job while it's held releases back to Ready (no executor tied up). Contrast 207-run-with-lock, which serializes only a section while the waiter holds its executor.
var first = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ExclusiveKey("rebuild:acme"));
var second = await jobs.EnqueueAsync(new RebuildIndex("acme"), o => o.ExclusiveKey("rebuild:acme"));
Console.WriteLine("Enqueued two jobs with the same exclusive key.");

long ownerJobId;
long competitorJobId;
using var admissionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (true)
{
    var firstStatus = (await jobs.GetAsync(first, admissionTimeout.Token))?.Status;
    var secondStatus = (await jobs.GetAsync(second, admissionTimeout.Token))?.Status;
    if (firstStatus == JobStatusCode.Executing && secondStatus == JobStatusCode.Ready)
    {
        ownerJobId = first.JobId;
        competitorJobId = second.JobId;
        break;
    }
    if (secondStatus == JobStatusCode.Executing && firstStatus == JobStatusCode.Ready)
    {
        ownerJobId = second.JobId;
        competitorJobId = first.JobId;
        break;
    }
    if (firstStatus?.IsTerminal == true && secondStatus?.IsTerminal == true)
    {
        throw new InvalidOperationException(
            "Both jobs finished before the admission boundary was observed; increase the lab handler delay."
        );
    }
    await Task.Delay(50, admissionTimeout.Token);
}
await lab.ShowAllAsync(
    "Explore both complete job records at the admission boundary",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id IN (@ownerJobId, @competitorJobId)
    ORDER BY CASE WHEN job_id = @ownerJobId THEN 0 ELSE 1 END
    """,
    new { ownerJobId, competitorJobId }
);
await lab.ShowAsync(
    "One job executes; its competitor returns to Ready without holding a worker",
    """
    SELECT job_id, job_ref, status, exclusive_key, leased_by_worker_id, next_run_at_utc
    FROM jobs_view
    WHERE job_id IN (@ownerJobId, @competitorJobId)
    ORDER BY CASE WHEN job_id = @ownerJobId THEN 0 ELSE 1 END
    """,
    new { ownerJobId, competitorJobId }
);
await lab.ShowAsync(
    "The exclusive key is a named lease owned by the running job",
    """
    SELECT lease_key, job_id, expires_at_utc, version
    FROM {{schema}}.leases
    WHERE job_id = @ownerJobId
    """,
    new { ownerJobId }
);

using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (
    (await jobs.GetAsync(first, completionTimeout.Token))?.Status.IsTerminal != true
    || (await jobs.GetAsync(second, completionTimeout.Token))?.Status.IsTerminal != true
)
{
    await Task.Delay(100, completionTimeout.Token);
}
await lab.ShowAsync(
    "The event ledger explains the budget-neutral admission bounce",
    """
    SELECT job_id, event, to_status, reason, execution_number
    FROM events_view
    WHERE job_id IN (@firstJobId, @secondJobId)
    ORDER BY event_id
    """,
    new { firstJobId = first.JobId, secondJobId = second.JobId }
);
await host.StopAsync();

namespace Acta.Concepts.ExclusiveKey
{
    public sealed record RebuildIndex(string Tenant);

    public sealed class RebuildIndexJob
    {
        private static int _n;

        [Job("rebuild-index")]
        public async Task Handle(RebuildIndex input, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _n);
            Console.WriteLine($"[{input.Tenant}] rebuild #{n} started");
            await Task.Delay(3000, ct);
            Console.WriteLine($"[{input.Tenant}] rebuild #{n} finished");
        }
    }
}
