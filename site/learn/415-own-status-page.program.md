# The whole program: show where each job is on your own page

The how-to is [/learn/415-own-status-page.md](/learn/415-own-status-page.md). This file holds every source file of `learn/400-observability-and-alerts/415-own-status-page` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### own-status-page.cs

```csharp
// How to show where each job is on your own page
// `IJobs` reads one job for a page of your own: its status, a value the handler keeps for the screen, and a sentence
// that says why the job is where it is.
using Acta;
using Acta.Learn.OwnStatusPage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<OwnStatusPageJobs>("own-status-page");
});

// The first attempt fails on purpose, to show a job between retries; quiet the framework's warning for it.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var order = await jobs.EnqueueAsync(new PrepareOrder("order-7"));

// The page is read three times: between two attempts, while the job waits for the warehouse, and when it is done.
await UntilAsync(job => job.Status == JobStatusCode.Ready && job.FailureCount > 0);
await ShowAsync();

await UntilAsync(job => job.Status == JobStatusCode.Suspended);
await ShowAsync();

await jobs.RaiseSignalAsync(order, "packed", JobPayload.None);
await order.WaitAsync();
await ShowAsync();

await host.StopAsync();

async Task ShowAsync()
{
    // learn: 2. Read the job, its stage, and the sentence that says why
    // `GetAsync` answers the job's row, `GetVariableOrDefaultAsync` the value its handler keeps for the screen, and
    // `ExplainAsync` a sentence a person can read. Each is a read of what the database holds now; none waits.
    var job = (await jobs.GetAsync(order))!;
    var stage = await jobs.GetVariableOrDefaultAsync<string>(order, "stage");
    var why = (await jobs.ExplainAsync(order))!;
    // learn: end

    // learn: 3. Say the status in the page's own words
    // A waiting job is healthy, a ready job that has failed before is between retries, and `Executing` means claimed,
    // not that a process is alive: `LeaseLapsed` is true for a job whose worker stopped heartbeating.
    var shown = job.Status switch
    {
        JobStatusCode.Suspended => "waiting",
        JobStatusCode.Ready when job.FailureCount > 0 => "retrying",
        JobStatusCode.Executing when job.LeaseLapsed => "interrupted",
        JobStatusCode.Executing => "running",
        _ => job.Status.Code,
    };
    // learn: end
    Console.WriteLine($"{shown, -9} | stage: {stage, -8} | {why.Headline}");
}

async Task UntilAsync(Func<GetJobResponse, bool> reached)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    while (await jobs.GetAsync(order, timeout.Token) is not { } job || !reached(job))
    {
        await Task.Delay(50, timeout.Token);
    }
}

namespace Acta.Learn.OwnStatusPage
{
    public sealed record PrepareOrder(string OrderId);

    public sealed class PrepareOrderJob
    {
        [Job("prepare-order", MaxAttempts = 3, Backoff = "2s")]
        public async Task Handle(PrepareOrder order, JobContext ctx, CancellationToken ct)
        {
            await ctx.RunStepAsync(
                "charge",
                async inner =>
                {
                    // learn: 1. Keep what the screen shows in a variable
                    // The status says where the job is in Acta's terms. What it is doing in your terms is a variable
                    // the handler writes as the work advances, inside the step, so a replay does not write it again.
                    await ctx.SetVariableAsync("stage", "charging", inner);
                    // learn: end
                    if (ctx.ExecutionNumber == 1)
                    {
                        throw new InvalidOperationException("the payment provider timed out");
                    }
                },
                ct
            );

            await ctx.RunStepAsync("hand-to-warehouse", async inner => await ctx.SetVariableAsync("stage", "packing", inner), ct);
            await ctx.WaitSignalAsync("packed", ct);
            await ctx.SetVariableAsync("stage", "shipped", ct);
        }
    }
}
```

