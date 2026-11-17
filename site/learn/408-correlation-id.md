# How to carry a correlation key from parent to child

A `CorrelationKey` set on the enqueue travels with the job and with every child it starts. It is on the job row, in each handler's log scope, and a filter of the job list.

- Section: Watch and control running work, step 13
- Program: `learn/400-observability-and-alerts/408-correlation-id` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/408-correlation-id
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Set the correlation key when you enqueue

JobExecutionOptions extends JobEnqueueOptions, so CorrelationKey is an enqueue-time option here.

The lines that matter:

```csharp
var outcome = await jobs.CallAsync<RunPipeline>(
    new RunPipeline("demo"),
    new JobExecutionOptions { CorrelationKey = correlationKey, PollInterval = TimeSpan.FromMilliseconds(200) }
);
```

In `correlation-id.cs` (excerpt):

```csharp
// The caller supplies a correlation id - a W3C trace id or any opaque string up to 64 chars.
// A child started with no explicit correlation id inherits the parent's automatically.
var correlationKey = $"trace-{Guid.NewGuid():N}";
Console.WriteLine($"enqueueing parent with CorrelationKey={correlationKey}");

var outcome = await jobs.CallAsync<RunPipeline>(
    new RunPipeline("demo"),
    new JobExecutionOptions { CorrelationKey = correlationKey, PollInterval = TimeSpan.FromMilliseconds(200) }
);
outcome.ThrowIfFailed();
Console.WriteLine("pipeline done");
```

## 2. Find every job of one trace

One list read finds the parent and the children that inherited its key. The program also captures the handlers' log scopes: every line carries `ActaCorrelationKey`.

The lines that matter:

```csharp
var trace = await operations.Ledger.ListJobsAsync(
    new ListJobsQuery(JobNamespace: "correlation-id", CorrelationKey: correlationKey, PageSize: 10)
);
```

In `correlation-id.cs` (excerpt):

```csharp
// Operator read: CorrelationKey is both a ListJobsQuery filter and a ListJobsItem field, so one list
// read pulls every job on a trace - parent and inherited children - with the id already on each row.
// (It is also on GetJobResponse via GetAsync when you want the full per-job detail.)
Console.WriteLine($"operator read: ListJobs filtered by CorrelationKey={correlationKey}");
var operations = host.Services.GetRequiredService<IActaOperations>();

var trace = await operations.Ledger.ListJobsAsync(
    new ListJobsQuery(JobNamespace: "correlation-id", CorrelationKey: correlationKey, PageSize: 10)
);
foreach (var item in trace.Items)
{
    Console.WriteLine($"  id={item.JobId} job={item.JobName} correlation_key={item.CorrelationKey}");
}

await host.StopAsync();
```

## 3. The job it acts on

`correlation-id.cs` (excerpt):

```csharp
public sealed class PipelineHandler
{
    private readonly ILogger<PipelineHandler> _log;

    public PipelineHandler(ILogger<PipelineHandler> log) => _log = log;

    // The framework opens a log scope with the job identity before calling this handler;
    // ActaCorrelationKey is one of the scope properties. Every log line below inherits it automatically.
    [Job("run-pipeline")]
    public async Task Handle(RunPipeline input, JobContext context, CancellationToken ct)
    {
        _log.LogInformation("parent handler: RunId={RunId}; look for ActaCorrelationKey in Scopes", input.RunId);

        await context.CallChildAsync("step-1", new ProcessStep(input.RunId, 1), ct);

        _log.LogInformation("parent handler complete");
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/400-observability-and-alerts/408-correlation-id
```

Its output must contain:

```text
same ActaCorrelationKey in Scopes
"ActaCorrelationKey":"trace-
pipeline done
job=process-step correlation_key=trace-
job=run-pipeline correlation_key=trace-
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to wrap every handler in your own middleware](/learn/401-pipeline-behavior.md)

## More

- [The whole program](/learn/408-correlation-id.program.md): every file as compiled, 112 lines.
