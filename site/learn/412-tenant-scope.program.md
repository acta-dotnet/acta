# The whole program: record which tenant a job is for

The how-to is [/learn/412-tenant-scope.md](/learn/412-tenant-scope.md). This file holds every source file of `learn/400-observability-and-alerts/412-tenant-scope` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### tenant-scope.cs

```csharp
// How to record which tenant a job is for
// `TenantName` on the enqueue records which tenant a job is for. Its children inherit the tenant, the job list
// filters by it, and an enqueue for an unknown or a suspended tenant is refused.
using Acta;
using Acta.Labs;
using Acta.Learn.TenantScope;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "tenant-scope";
var builder = Host.CreateApplicationBuilder(args);
var lab = new LearnLab(builder.Configuration, args);
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

// learn: 1. Register the tenant
// A tenant is a row in the catalog. An enqueue names it, and the name must resolve to a tenant that is not suspended.
var acmeId = (await operations.Tenants.RegisterAsync("acme", "Acme GmbH", "Active customer used by the lab")).TenantId;

// learn: end
await operations.Tenants.RegisterAsync("held-customer", "Held customer");
await operations.Tenants.SuspendAsync("held-customer", "Held for the lab's rejection demo");

var runId = $"tenant-scope-{Guid.CreateVersion7():N}";

// learn: 2. Name the tenant when you enqueue
// An enqueue that names an unknown or a suspended tenant throws `EnqueueRejectedException`, and its `Reason` says
// which.
var parent = await jobs.EnqueueAsync(new ExportAccount("acme"), options => options.TenantName("acme").CorrelationKey(runId));

// learn: end
var parentTerminal = await WaitForAsync(jobs, parent.JobId, static snapshot => snapshot.Status.IsFinished, "tenant parent to finish");
if (parentTerminal.Status != JobStatusCode.Succeeded)
{
    throw new InvalidOperationException($"Tenant parent {parentTerminal.JobRef} ended {parentTerminal.Status}.");
}

// learn: 3. List the jobs of one tenant
// The filter finds the parent and the child that inherited its tenant.
var byTenant = await operations.Ledger.ListJobsAsync(
    new ListJobsQuery(JobNamespace: jobNamespace, TenantId: acmeId, CorrelationKey: runId, IncludeTotal: true)
);

// learn: end
Console.WriteLine(
    $"Ledger tenant + correlation filter returned {byTenant.TotalCount} current-run Acme job(s): parent and inherited child."
);

foreach (var rejectedKey in new[] { "missing-customer", "held-customer" })
{
    try
    {
        await jobs.EnqueueAsync(new ExportAccount(rejectedKey), options => options.TenantName(rejectedKey));
    }
    catch (EnqueueRejectedException rejected)
    {
        Console.WriteLine($"enqueue for '{rejectedKey}' rejected: {rejected.Reason}");
    }
}

if (suspendActive)
{
    var preExisting = await jobs.EnqueueAsync(new TenantLifecycleProbe("acme"), options =>
        options.TenantName("acme").CorrelationKey(runId).Delayed(TimeSpan.FromSeconds(2))
    );
    await operations.Tenants.SuspendAsync("acme", "tenant lifecycle lab", "lab-user");
    try
    {
        try
        {
            await jobs.EnqueueAsync(new TenantLifecycleProbe("acme"), options => options.TenantName("acme"));
            throw new InvalidOperationException("Expected enqueue for the newly suspended tenant to be rejected.");
        }
        catch (EnqueueRejectedException rejected)
        {
            Console.WriteLine($"enqueue after suspending 'acme' rejected: {rejected.Reason}");
        }

        var preExistingTerminal = await WaitForAsync(
            jobs,
            preExisting.JobId,
            static snapshot => snapshot.Status.IsFinished,
            "work accepted before tenant suspension to finish"
        );
        if (preExistingTerminal.Status != JobStatusCode.Succeeded)
        {
            throw new InvalidOperationException($"Pre-existing tenant work ended {preExistingTerminal.Status}.");
        }
        Console.WriteLine($"work accepted before suspension ended {preExistingTerminal.Status}; suspension did not cancel it.");
    }
    finally
    {
        await operations.Tenants.ResumeAsync("acme", "restore repeatable lab state", "lab-user");
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
    SELECT j.job_id, j.definition_name, j.parent_job_id, j.lineage_root_job_id, t.tenant_name, j.job_status
    FROM jobs_view AS j
    LEFT JOIN {{schema}}.tenants AS t ON t.tenant_id = j.tenant_id
    WHERE j.lineage_root_job_id = @rootJobId
    ORDER BY j.job_id
    """,
    new { rootJobId = parent.JobId }
);
await lab.ShowAsync(
    "Tenant scope is stamped into the append-only execution evidence",
    """
    SELECT e.entity_id AS job_id, e.definition_name, e.tenant_name, e.event, e.details_text
    FROM events_view AS e
    WHERE e.lineage_root_job_id = @rootJobId
    ORDER BY e.event_id
    """,
    new { rootJobId = parent.JobId }
);

await host.StopAsync();

static async Task<GetJobResponse> WaitForAsync(IJobs jobs, long jobId, Func<GetJobResponse, bool> predicate, string description)
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

namespace Acta.Learn.TenantScope
{
    public sealed record ExportAccount(string TenantName);

    public sealed record WriteExport(string TenantName);

    public sealed record TenantLifecycleProbe(string TenantName);

    public static class ExportAccountJob
    {
        [Job("export-account")]
        public static async Task Handle(ExportAccount input, JobContext context, CancellationToken ct)
        {
            // No TenantName override: the child inherits the parent's resolved tenant_id.
            await context.CallChildAsync("write-export", new WriteExport(input.TenantName), ct);
        }

        [Job("write-export")]
        public static void Write(WriteExport input) => Console.WriteLine($"wrote account export for {input.TenantName}");

        [Job("tenant-lifecycle-probe")]
        public static void Probe(TenantLifecycleProbe input) =>
            Console.WriteLine($"work accepted before suspension still ran for {input.TenantName}");
    }
}
```

## Notes

### The problem

Multi-tenant work needs filtering, audit attribution, lifecycle guards, and child inheritance. A string
tag can label work, but it cannot reject an unknown or suspended customer atomically at enqueue.

### Common approaches

- Use a namespace/queue per tenant.
- Use a database per tenant for hard isolation.
- Partition a broker by tenant name.
- Attach unvalidated tags only.
- Resolve a tenant name through an Acta-owned catalog.

### Why this design

Namespace answers “who owns and executes this work?” Tenant answers “who is this work about?” Acta
resolves an active `TenantName` to `tenant_id` on insert, stamps events, inherits it to children, exposes
tenant filtering, and rejects unknown or suspended tenants.

### Trade-offs

A shared database/catalog is an audit and control boundary, not hard data isolation. Existing in-flight
jobs are not cancelled when a tenant is suspended. Tenant cardinality and query/index pressure still
land on the shared store.

### Run the experiment

```bash
dotnet run --project learn/400-observability-and-alerts/412-tenant-scope
```

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

The lab uses `jobs_view` and `events_view`, joining the internal tenant catalog only to decode the tenant name.
It proves parent/child inheritance and also exercises the operator ledger read `IActaOperations.Ledger.ListJobsAsync` with its tenant filter and a fresh
correlation key, so repeated runs still report exactly the current parent and child. Application code
should use `IActaOperations.Tenants` and `IJobs`; the underlying catalog table is not a compatibility contract.

### Break it

The default run rejects both an unknown and an already suspended tenant. Also run:

```bash
dotnet run --project learn/400-observability-and-alerts/412-tenant-scope -- --suspend-active
```

The mode accepts one delayed job while `acme` is active, suspends the tenant, proves a new enqueue is
rejected, and then proves the already accepted job was not cancelled. It resumes `acme` afterward so
the shared local database remains repeatable.

### When not to use

Use separate databases or stronger infrastructure isolation for regulatory/data-residency boundaries.
Use queue partitioning when tenant ordering and throughput isolation are the primary goals. Tags are
enough when the label needs no validation or lifecycle.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`tenant-scope.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/400-observability-and-alerts/412-tenant-scope/tenant-scope.cs)
- [`ITenants.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Operations/Tenants/ITenants.cs)
- [`EnqueueJobTenantSpec.cs`](https://github.com/acta-dotnet/acta/blob/main/tests/Acta.Tests.Conformance/Operations/Jobs/EnqueueJobTenantSpec.cs)
