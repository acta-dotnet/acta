# How to record which tenant a job is for

`TenantName` on the enqueue records which tenant a job is for. Its children inherit the tenant, the job list filters by it, and an enqueue for an unknown or a suspended tenant is refused.

- Section: Watch and control running work, step 12
- Program: `learn/400-observability-and-alerts/412-tenant-scope` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/412-tenant-scope
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Register the tenant

A tenant is a row in the catalog. An enqueue names it, and the name must resolve to a tenant that is not suspended.

The lines that matter:

```csharp
var acmeId = (await operations.Tenants.RegisterAsync("acme", "Acme GmbH", "Active customer used by the lab")).TenantId;
```

In `tenant-scope.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();
var jobs = host.Services.GetRequiredService<IJobs>();
var operations = host.Services.GetRequiredService<IActaOperations>();

var acmeId = (await operations.Tenants.RegisterAsync("acme", "Acme GmbH", "Active customer used by the lab")).TenantId;
await operations.Tenants.RegisterAsync("held-customer", "Held customer");
await operations.Tenants.SuspendAsync("held-customer", "Held for the lab's rejection demo");

var runId = $"tenant-scope-{Guid.CreateVersion7():N}";
```

## 2. Name the tenant when you enqueue

An enqueue that names an unknown or a suspended tenant throws `EnqueueRejectedException`, and its `Reason` says which.

The lines that matter:

```csharp
var parent = await jobs.EnqueueAsync(new ExportAccount("acme"), options => options.TenantName("acme").CorrelationKey(runId));
```

In `tenant-scope.cs` (excerpt):

```csharp
var acmeId = (await operations.Tenants.RegisterAsync("acme", "Acme GmbH", "Active customer used by the lab")).TenantId;
await operations.Tenants.RegisterAsync("held-customer", "Held customer");
await operations.Tenants.SuspendAsync("held-customer", "Held for the lab's rejection demo");

var runId = $"tenant-scope-{Guid.CreateVersion7():N}";

var parent = await jobs.EnqueueAsync(new ExportAccount("acme"), options => options.TenantName("acme").CorrelationKey(runId));
var parentTerminal = await WaitForAsync(jobs, parent.JobId, static snapshot => snapshot.Status.IsTerminal, "tenant parent to finish");
if (parentTerminal.Status != JobStatusCode.Succeeded)
{
    throw new InvalidOperationException($"Tenant parent {parentTerminal.JobRef} ended {parentTerminal.Status}.");
}
```

## 3. List the jobs of one tenant

The filter finds the parent and the child that inherited its tenant.

The lines that matter:

```csharp
var byTenant = await operations.Ledger.ListJobsAsync(
    new ListJobsQuery(JobNamespace: jobNamespace, TenantId: acmeId, CorrelationKey: runId, IncludeTotal: true)
);
```

In `tenant-scope.cs` (excerpt):

```csharp
var byTenant = await operations.Ledger.ListJobsAsync(
    new ListJobsQuery(JobNamespace: jobNamespace, TenantId: acmeId, CorrelationKey: runId, IncludeTotal: true)
);
Console.WriteLine(
    $"Ledger tenant + correlation filter returned {byTenant.TotalCount} current-run Acme job(s): parent and inherited child."
);
```

## 4. The job it acts on

`tenant-scope.cs` (excerpt):

```csharp
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
```

## 5. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/400-observability-and-alerts/412-tenant-scope
```

Its output must contain:

```text
wrote account export for acme
returned 2 current-run Acme job(s): parent and inherited child.
enqueue for 'missing-customer' rejected: TenantUnknown
enqueue for 'held-customer' rejected: TenantSuspended
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to carry a correlation key from parent to child](/learn/408-correlation-id.md)

## More

- [The whole program and its notes](/learn/412-tenant-scope.program.md): every file as compiled, 186 lines, and the notes on the design, the rows to inspect, and how to break it.
