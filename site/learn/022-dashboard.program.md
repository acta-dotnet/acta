# The whole program: see your jobs in the dashboard

The how-to is [/learn/022-dashboard.md](/learn/022-dashboard.md). This file holds every source file of `learn/000-fundamentals/022-dashboard` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### Program.cs

```csharp
// How to see your jobs in the dashboard
// `MapActa` serves the dashboard and its JSON API from your own app. Its controls are off until you turn them on.
using Acta;
using Acta.Dashboard;
using Acta.Labs;
using Acta.Learn.DashboardSample;

var builder = WebApplication.CreateBuilder(args);
var lab = new LearnLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DashboardSampleJobs>("dashboard-sample");
});

// The failed/retrying rows are intentional fixtures; preserve their evidence without startup stack-trace noise.
builder.Logging.AddFilter("Acta", LogLevel.Error);

var app = builder.Build();

// learn: Map the dashboard into your app
// `MapActa` adds the dashboard and its JSON API under the path you give. By default it answers local requests only,
// and its controls (pause, resume, cancel, restart) stay disabled until `EnableControls` is set.
var enableControls = builder.Configuration.GetValue<bool>("Acta:Dashboard:EnableControls");
app.MapActa("/acta", options => options.EnableControls = enableControls);

// learn: end

await app.StartAsync();

var jobs = app.Services.GetRequiredService<IJobs>();
var successful = await jobs.EnqueueAsync(new GreetVisitor("successful-visitor"));
var failed = await jobs.EnqueueAsync(new BrokenImport("broken.csv"));
var retrying = await jobs.EnqueueAsync(new RetryImport("upstream.csv"));
var waiting = await jobs.EnqueueAsync(new AwaitApproval("invoice-42"));
var parent = await jobs.EnqueueAsync(new BuildPreview("catalog-7"));

await Task.Delay(1500);
await lab.ShowAllAsync(
    "Explore the complete records behind the dashboard",
    """
    SELECT *
    FROM jobs_view
    WHERE job_id IN (@successful, @failed, @retrying, @waiting, @parent)
    ORDER BY job_id
    """,
    new
    {
        successful = successful.JobId,
        failed = failed.JobId,
        retrying = retrying.JobId,
        waiting = waiting.JobId,
        parent = parent.JobId,
    }
);
await lab.ShowAsync(
    "The dashboard reads the same varied durable states as the operator views",
    """
    SELECT job_ref, definition_name, job_status, parent_job_id, execution_number, failure_count
    FROM jobs_view
    WHERE job_id IN (@successful, @failed, @retrying, @waiting, @parent)
    ORDER BY job_id
    """,
    new
    {
        successful = successful.JobId,
        failed = failed.JobId,
        retrying = retrying.JobId,
        waiting = waiting.JobId,
        parent = parent.JobId,
    }
);

Console.WriteLine();
Console.WriteLine("Seeded success, failure, retry, signal, recurring, parent, and child states for inspection.");
Console.WriteLine($"Dashboard: {app.Urls.FirstOrDefault() ?? "http://localhost:5000"}/acta");
Console.WriteLine($"Dashboard controls: {(enableControls ? "ENABLED" : "DISABLED")}");
if (!enableControls)
{
    Console.WriteLine("Set Acta__Dashboard__EnableControls=true to enable local controls intentionally.");
}
Console.WriteLine("Press Ctrl+C to stop.");

await app.WaitForShutdownAsync();

namespace Acta.Learn.DashboardSample
{
    public sealed record GreetVisitor(string Name);

    public readonly record struct HeartbeatTick;

    public sealed record BrokenImport(string FileName);

    public sealed record RetryImport(string FileName);

    public sealed record AwaitApproval(string InvoiceId);

    public sealed record BuildPreview(string CatalogId);

    public sealed record RenderThumbnail(string CatalogId);

    public static class GreetVisitorJob
    {
        [Job("greet-visitor")]
        public static async Task<string> Handle(GreetVisitor input, CancellationToken ct)
        {
            await Task.Delay(200, ct);
            return $"Hello, {input.Name}!";
        }
    }

    public static class BrokenImportJob
    {
        [Job("broken-import", MaxAttempts = 1)]
        public static Task Handle(BrokenImport input) => throw new InvalidDataException($"{input.FileName}: invalid header");
    }

    public static class RetryImportJob
    {
        [Job("retry-import", MaxAttempts = 5, Backoff = "30s")]
        public static Task Handle(RetryImport input) => throw new IOException($"{input.FileName}: upstream unavailable");
    }

    public static class AwaitApprovalJob
    {
        [Job("await-approval")]
        public static async Task Handle(AwaitApproval input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine($"[{input.InvoiceId}] waiting for approval");
            await context.WaitSignalAsync("approval", ct);
        }
    }

    public static class PreviewJob
    {
        [Job("build-preview")]
        public static async Task Handle(BuildPreview input, JobContext context, CancellationToken ct)
        {
            await context.CallChildAsync("thumbnail", new RenderThumbnail(input.CatalogId), ct);
        }

        [Job("render-thumbnail")]
        public static async Task Handle(RenderThumbnail input, CancellationToken ct)
        {
            await Task.Delay(4000, ct);
            Console.WriteLine($"[{input.CatalogId}] thumbnail rendered");
        }
    }

    public sealed class HeartbeatJob
    {
        [Job("heartbeat")]
        [Schedule("every-15-seconds", Cron.Every15Seconds)]
        public Task Handle(HeartbeatTick input) => Task.Delay(50);
    }
}
```

## Notes

### The problem

A useful job UI needs representative runtime states, but mapping mutation endpoints casually can turn an
observability feature into an unaudited production control surface.

### Common approaches

- Deploy a separate dashboard/control service.
- Embed a read-only UI beside the application.
- Enable controls explicitly and protect them with operator authorization.
- Build a domain-specific operator API and UI.

### Why this design

`MapActa` ships the API and dashboard assets with the app that owns the jobs. This lab seeds success,
failure, retry, signal-waiting, recurring, parent, and child states. Controls remain disabled unless the
configuration switch is intentionally set. Expected handler warnings are suppressed in the console so
the startup URL remains obvious; their durable failures and full event evidence remain in Acta.

### Trade-offs

Embedding ties dashboard lifecycle and resource use to the host. It is not a substitute for centralized
authentication, authorization, network policy, or fleet-wide aggregation. Enabling controls expands the
application's operational attack surface.

### Run the experiment

```bash
dotnet run --project learn/000-fundamentals/022-dashboard
```

Then opt in locally and compare:

```bash
Acta__Dashboard__EnableControls=true dotnet run --project learn/000-fundamentals/022-dashboard
```

In PowerShell, set `$env:Acta__Dashboard__EnableControls='true'` first.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

The startup query shows the same `jobs_view` states rendered by the UI. Explore events, schedules,
workers, alerts, lineage, and explanations in the dashboard. These curated surfaces are for operations;
the internal storage tables are not a public compatibility contract.

### Break it

Try a mutation while controls are disabled, then opt in and repeat it confirmed, with the header
`X-Acta-Control: true`, a JSON content type, or a Bearer authorization header, or through the UI
flow; a body member the endpoint does not take answers 400.
The surface is local-only: a request must come from loopback and name the host as `localhost` or an IP
address, so browse to `localhost`, not the machine name. Binding beyond localhost exposes nothing by
itself; serving remote callers takes `LocalOnly = false` plus host authorization through
`ConfigureEndpoints`, so try it only in a deliberately secured test environment.

### When not to use

Use a centralized operations product when one UI must span many deployments, databases, or trust zones.
Build a custom UI when operators need domain-specific workflows rather than generic job controls.

### Source trail

- [The controls Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [The embedded-dashboard Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`Program.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/000-fundamentals/022-dashboard/Program.cs)
- [`ActaEndpointOptions.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta.AspNetCore/Configuration/ActaEndpointOptions.cs)
- [`JobsEndpoints.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta.AspNetCore/Operations/Jobs/JobsEndpoints.cs)
