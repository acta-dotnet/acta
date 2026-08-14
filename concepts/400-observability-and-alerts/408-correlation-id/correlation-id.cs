// Concept: CorrelationKey threads a caller-supplied trace id through a parent job and all its
// children; the id lives on the job row and in every handler's log scope for cross-system tracing.
using Acta;
using Acta.Concepts.CorrelationKey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CorrelationKeyJobs>("correlation-id");
});

// Log the concept's own handler output at Information so the log-scope demo is visible.
// UseLocalDatabase sets the global minimum to Warning; AddFilter narrows the override to this namespace.
// AddJsonConsole renders structured scope properties (including CorrelationKey) as key-value pairs.
builder.Services.AddLogging(l =>
{
    l.AddFilter("Acta.Concepts.CorrelationKey", LogLevel.Information);
    l.AddJsonConsole(o => o.IncludeScopes = true);
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// The caller supplies a correlation id - a W3C trace id or any opaque string up to 64 chars.
// A child started with no explicit correlation id inherits the parent's automatically.
var correlationKey = $"trace-{Guid.NewGuid():N}";
Console.WriteLine($"enqueueing parent with CorrelationKey={correlationKey}");

// JobExecutionOptions extends JobEnqueueOptions, so CorrelationKey is an enqueue-time option here.
var outcome = await jobs.ExecuteAndWaitAsync<RunPipeline>(
    new RunPipeline("demo"),
    new JobExecutionOptions { CorrelationKey = correlationKey, PollInterval = TimeSpan.FromMilliseconds(200) }
);
outcome.ThrowIfFailed();
Console.WriteLine("pipeline done");

// Operator read: CorrelationKey is both a ListJobsQuery filter and a JobListItem field, so one list
// read pulls every job on a trace - parent and inherited children - with the id already on each row.
// (It is also on JobDetail via GetAsync when you want the full per-job detail.)
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

namespace Acta.Concepts.CorrelationKey
{
    public sealed record RunPipeline(string RunId);

    public sealed record ProcessStep(string RunId, int StepNumber);

    public sealed class PipelineHandler
    {
        private readonly ILogger<PipelineHandler> _log;

        public PipelineHandler(ILogger<PipelineHandler> log) => _log = log;

        // The framework opens a log scope with the job identity before calling this handler;
        // CorrelationKey is one of the scope properties. Every log line below inherits it automatically.
        [Job("run-pipeline")]
        public async Task Handle(RunPipeline input, JobContext context, CancellationToken ct)
        {
            _log.LogInformation("parent handler: RunId={RunId}; look for CorrelationKey in Scopes", input.RunId);

            var child = await context.StartChildAsync("step-1", new ProcessStep(input.RunId, 1), ct: ct);
            await context.WaitChildAsync(child.JobId, ct);

            _log.LogInformation("parent handler complete");
        }
    }

    public sealed class StepHandler
    {
        private readonly ILogger<StepHandler> _log;

        public StepHandler(ILogger<StepHandler> log) => _log = log;

        // The child inherits the parent's correlation id at enqueue (stored on its own job row).
        // The framework opens the same CorrelationKey scope for the child's attempt.
        [Job("process-step")]
        public void Handle(ProcessStep input)
        {
            _log.LogInformation(
                "child handler: RunId={RunId} Step={StepNumber}; same CorrelationKey in Scopes",
                input.RunId,
                input.StepNumber
            );
        }
    }
}
