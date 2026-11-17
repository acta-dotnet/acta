# How to see your jobs in the dashboard

`MapActa` serves the dashboard and its JSON API from your own app. Its controls are off until you turn them on.

- Section: Watch and control running work, step 2
- Program: `concepts/000-fundamentals/022-dashboard` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/022-dashboard
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Map the dashboard into your app

`MapActa` adds the dashboard and its JSON API under the path you give. By default it answers local requests only, and its controls (pause, resume, cancel, restart) stay disabled until `EnableControls` is set.

The lines that matter:

```csharp
var enableControls = builder.Configuration.GetValue<bool>("Acta:Dashboard:EnableControls");
app.MapActa("/acta", options => options.EnableControls = enableControls);
```

In `Program.cs` (excerpt):

```csharp
var builder = WebApplication.CreateBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DashboardSampleJobs>("dashboard-sample");
});

// The failed/retrying rows are intentional fixtures; preserve their evidence without startup stack-trace noise.
builder.Logging.AddFilter("Acta", LogLevel.Error);

var app = builder.Build();

var enableControls = builder.Configuration.GetValue<bool>("Acta:Dashboard:EnableControls");
app.MapActa("/acta", options => options.EnableControls = enableControls);

await app.StartAsync();
```

## 2. The job it acts on

`Program.cs` (excerpt):

```csharp
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
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/022-dashboard
```

Its output must contain:

```text
[invoice-42] waiting for approval
Seeded success, failure, retry, signal, recurring, parent, and child states for inspection.
Dashboard: http://127.0.0.1:
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` and `ConceptLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to read why a job did what it did](/learn/404-read-event-timeline.md)

## More

- [The whole program and its notes](/learn/022-dashboard.program.md): every file as compiled, 157 lines, and the notes on the design, the rows to inspect, and how to break it.
