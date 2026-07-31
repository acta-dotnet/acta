using Acta;
using Acta.Concepts.TenantScope;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "tenant-scope";
var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);
var suspendActive = args.Contains("--suspend-active", StringComparer.OrdinalIgnoreCase);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<TenantScopeJobs>(jobNamespace);
});

using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();
var operations = host.Services.GetRequiredService<IActaOperations>();

var acmeId = await operations.Tenants.RegisterAsync("acme", "Acme GmbH", "Active customer used by the lab");
await operations.Tenants.RegisterAsync("held-customer", "Held customer");
await operations.Tenants.SuspendAsync("held-customer", "Held for the lab's rejection demo");

var runId = $"tenant-scope-{Guid.CreateVersion7():N}";
var parent = await jobs.EnqueueAsync(new ExportAccount("acme"), options => options.TenantKey("acme").CorrelationKey(runId));
var parentTerminal = await WaitForAsync(jobs, parent.JobId, static snapshot => snapshot.Status.IsTerminal, "tenant parent to finish");
if (parentTerminal.Status != JobStatusCode.Done)
{
    throw new InvalidOperationException($"Tenant parent {parentTerminal.JobRef} ended {parentTerminal.Status}.");
}

var byTenant = await operations.ListJobsAsync(
    new ListJobsQuery(JobNamespace: jobNamespace, TenantId: acmeId, CorrelationKey: runId, IncludeTotal: true)
);
Console.WriteLine($"IJobs tenant + correlation filter returned {byTenant.TotalCount} current-run Acme job(s): parent and inherited child.");

foreach (var rejectedKey in new[] { "missing-customer", "held-customer" })
{
    try
    {
        await jobs.EnqueueAsync(new ExportAccount(rejectedKey), options => options.TenantKey(rejectedKey));
    }
    catch (EnqueueRejectedException rejected)
    {
        Console.WriteLine($"enqueue for '{rejectedKey}' rejected: {rejected.Reason}");
    }
}

if (suspendActive)
{
    var preExisting = await jobs.EnqueueAsync(
        new TenantLifecycleProbe("acme"),
        options => options.TenantKey("acme").CorrelationKey(runId).Delayed(TimeSpan.FromSeconds(2))
    );
    await operations.Tenants.SuspendAsync("acme", "tenant lifecycle lab", "concept-user");
    try
    {
        try
        {
            await jobs.EnqueueAsync(new TenantLifecycleProbe("acme"), options => options.TenantKey("acme"));
            throw new InvalidOperationException("Expected enqueue for the newly suspended tenant to be rejected.");
        }
        catch (EnqueueRejectedException rejected)
        {
            Console.WriteLine($"enqueue after suspending 'acme' rejected: {rejected.Reason}");
        }

        var preExistingTerminal = await WaitForAsync(
            jobs,
            preExisting.JobId,
            static snapshot => snapshot.Status.IsTerminal,
            "work accepted before tenant suspension to finish"
        );
        if (preExistingTerminal.Status != JobStatusCode.Done)
        {
            throw new InvalidOperationException($"Pre-existing tenant work ended {preExistingTerminal.Status}.");
        }
        Console.WriteLine($"work accepted before suspension ended {preExistingTerminal.Status}; suspension did not cancel it.");
    }
    finally
    {
        await operations.Tenants.ResumeAsync("acme", "restore repeatable lab state", "concept-user");
    }
}

await lab.ShowAllAsync(
    "Explore the complete tenant-stamped job records",
    """
    SELECT *
    FROM jobs_view
    WHERE lineage_root_job_id = @rootJobId
    ORDER BY job_id
    """,
    new { rootJobId = parent.JobId }
);
await lab.ShowAsync(
    "Namespace owns execution; tenant identifies who the parent and child are about",
    """
    SELECT j.job_id, j.job_name, j.parent_id, j.lineage_root_job_id, t.tenant_key, j.status
    FROM jobs_view AS j
    LEFT JOIN {{schema}}.tenants AS t ON t.id = j.tenant_id
    WHERE j.lineage_root_job_id = @rootJobId
    ORDER BY j.job_id
    """,
    new { rootJobId = parent.JobId }
);
await lab.ShowAsync(
    "Tenant scope is stamped into the append-only execution evidence",
    """
    SELECT e.job_id, e.job_name, t.tenant_key, e.event, e.to_status
    FROM events_view AS e
    LEFT JOIN {{schema}}.tenants AS t ON t.id = e.tenant_id
    WHERE e.lineage_root_job_id = @rootJobId
    ORDER BY e.event_id
    """,
    new { rootJobId = parent.JobId }
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
            await Task.Delay(50, timeout.Token);
        }
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        throw new TimeoutException($"Timed out waiting for {description} on job {jobId}.");
    }
}

namespace Acta.Concepts.TenantScope
{
    public sealed record ExportAccount(string TenantKey);

    public sealed record WriteExport(string TenantKey);

    public sealed record TenantLifecycleProbe(string TenantKey);

    public static class ExportAccountJob
    {
        [Job("export-account")]
        public static async Task Handle(ExportAccount input, JobContext ctx, CancellationToken ct)
        {
            // No TenantKey override: the child inherits the parent's resolved tenant_id.
            var child = await ctx.StartChildAsync("write-export", new WriteExport(input.TenantKey), ct: ct);
            await ctx.WaitChildrenAsync([child.JobId], ct);
        }

        [Job("write-export")]
        public static void Write(WriteExport input) => Console.WriteLine($"wrote account export for {input.TenantKey}");

        [Job("tenant-lifecycle-probe")]
        public static void Probe(TenantLifecycleProbe input) =>
            Console.WriteLine($"work accepted before suspension still ran for {input.TenantKey}");
    }
}
