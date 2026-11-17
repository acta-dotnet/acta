# The whole program: carry a correlation key from parent to child

The how-to is [/learn/408-correlation-id.md](/learn/408-correlation-id.md). This file holds every source file of `learn/400-observability-and-alerts/408-correlation-id` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### correlation-id.cs

```csharp
// How to carry a correlation key from parent to child
// A `CorrelationKey` set on the enqueue travels with the job and with every child it starts. It is on the job row, in
// each handler's log scope, and a filter of the job list.
using Acta;
using Acta.Learn.CorrelationKey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CorrelationKeyJobs>("correlation-id");
});

// Log the program's own handler output at Information so the log-scope demo is visible.
// UseLocalDatabase sets the global minimum to Warning; AddFilter narrows the override to this namespace.
// AddJsonConsole renders structured scope properties (including ActaCorrelationKey) as key-value pairs.
builder.Services.AddLogging(l =>
{
    l.AddFilter("Acta.Learn.CorrelationKey", LogLevel.Information);
    l.AddJsonConsole(o => o.IncludeScopes = true);
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// The caller supplies a correlation id - a W3C trace id or any opaque string up to 64 chars.
// A child started with no explicit correlation id inherits the parent's automatically.
var correlationKey = $"trace-{Guid.NewGuid():N}";
Console.WriteLine($"enqueueing parent with CorrelationKey={correlationKey}");

// learn: 1. Set the correlation key when you enqueue
// JobExecutionOptions extends JobEnqueueOptions, so CorrelationKey is an enqueue-time option here.
var outcome = await jobs.CallAsync<RunPipeline>(
    new RunPipeline("demo"),
    new JobExecutionOptions { CorrelationKey = correlationKey, PollInterval = TimeSpan.FromMilliseconds(200) }
);

// learn: end
outcome.ThrowIfFailed();
Console.WriteLine("pipeline done");

// Operator read: CorrelationKey is both a ListJobsQuery filter and a ListJobsItem field, so one list
// read pulls every job on a trace - parent and inherited children - with the id already on each row.
// (It is also on GetJobResponse via GetAsync when you want the full per-job detail.)
Console.WriteLine($"operator read: ListJobs filtered by CorrelationKey={correlationKey}");
var operations = host.Services.GetRequiredService<IActaOperations>();

// learn: 2. Find every job of one trace
// One list read finds the parent and the children that inherited its key. The program also captures the handlers' log
// scopes: every line carries `ActaCorrelationKey`.
var trace = await operations.Ledger.ListJobsAsync(
    new ListJobsQuery(JobNamespace: "correlation-id", CorrelationKey: correlationKey, PageSize: 10)
);

// learn: end
foreach (var item in trace.Items)
{
    Console.WriteLine($"  id={item.JobId} job={item.JobName} correlation_key={item.CorrelationKey}");
}

await host.StopAsync();

namespace Acta.Learn.CorrelationKey
{
    public sealed record RunPipeline(string RunId);

    public sealed record ProcessStep(string RunId, int StepNumber);

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

    public sealed class StepHandler
    {
        private readonly ILogger<StepHandler> _log;

        public StepHandler(ILogger<StepHandler> log) => _log = log;

        // The child inherits the parent's correlation id at enqueue (stored on its own job row).
        // The framework opens the same ActaCorrelationKey scope for the child's attempt.
        [Job("process-step")]
        public void Handle(ProcessStep input)
        {
            _log.LogInformation(
                "child handler: RunId={RunId} Step={StepNumber}; same ActaCorrelationKey in Scopes",
                input.RunId,
                input.StepNumber
            );
        }
    }
}
```

