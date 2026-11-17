# The whole program: read the outcome of a wait that can time out

The how-to is [/learn/311-call-outcome-timeout.md](/learn/311-call-outcome-timeout.md). This file holds every source file of `learn/300-failure-and-recovery/311-call-outcome-timeout` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### call-outcome-timeout.cs

```csharp
// How to read the outcome of a wait that can time out
// `CallAsync` answers a `JobOutcome`: whether the wait timed out, whether the job failed, and the value when it
// succeeded.
using Acta;
using Acta.Learn.CallOutcomeTimeout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CallOutcomeTimeoutJobs>("call-outcome-timeout");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

Console.WriteLine("--- (a) timeout ---");

// learn: 1. Bound the wait and see whether it timed out
// A slow handler with a short WaitTimeout: outcome.TimedOut is true.
// The job keeps running on the worker; the caller just stops waiting.
var opts = new JobExecutionOptions { WaitTimeout = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(50) };
var timedOut = await jobs.CallAsync(CallOutcomeTimeoutJobs.SlowReport(), opts);
Console.WriteLine($"TimedOut: {timedOut.TimedOut}");

// learn: end
Console.WriteLine($"Succeeded: {timedOut.Succeeded}");

Console.WriteLine("--- (b) failure ---");

// learn: 2. Read a job that failed
// A failing handler: Status is Failed, ThrowIfFailed throws JobFailedException, and TryGetValue is false.
var failed = await jobs.CallAsync(CallOutcomeTimeoutJobs.FailingReport());
Console.WriteLine($"Status: {failed.Status}");

// learn: end
try
{
    failed.ThrowIfFailed();
}
catch (JobFailedException ex)
{
    Console.WriteLine($"ThrowIfFailed threw: {ex.GetType().Name} job={ex.JobRef} status={ex.TerminalStatus}");
}
Console.WriteLine($"TryGetValue: {failed.TryGetValue(out _)}");

Console.WriteLine("--- (c) success ---");

// learn: 3. Take the value of a job that succeeded
// On success, ValueOrThrow returns the handler's result.
var success = await jobs.CallAsync(CallOutcomeTimeoutJobs.SuccessReport());
Console.WriteLine($"Succeeded: {success.Succeeded}");
var result = success.ValueOrThrow();

// learn: end
Console.WriteLine($"ValueOrThrow: {result.Summary}");

await host.StopAsync();

namespace Acta.Learn.CallOutcomeTimeout
{
    public readonly record struct SlowReport;

    public readonly record struct FailingReport;

    public readonly record struct SuccessReport;

    public sealed record ReportResult(string Summary);

    public static class SlowReportJob
    {
        [Job("slow-report")]
        public static async Task<ReportResult> Handle(SlowReport input, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new ReportResult("done");
        }
    }

    public static class FailingReportJob
    {
        [Job("failing-report", MaxAttempts = 1)]
        public static Task<ReportResult> Handle(FailingReport input)
        {
            throw new InvalidOperationException("report source unavailable");
        }
    }

    public static class SuccessReportJob
    {
        [Job("success-report")]
        public static Task<ReportResult> Handle(SuccessReport input)
        {
            return Task.FromResult(new ReportResult("all clear"));
        }
    }
}
```

