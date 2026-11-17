# How to read the outcome of a wait that can time out

`CallAsync` answers a `JobOutcome`: whether the wait timed out, whether the job failed, and the value when it succeeded.

- Section: Wait for people, events, and time, step 4
- Program: `concepts/300-failure-and-recovery/311-call-outcome-timeout` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/311-call-outcome-timeout
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Bound the wait and see whether it timed out

A slow handler with a short WaitTimeout: outcome.TimedOut is true. The job keeps running on the worker; the caller just stops waiting.

The lines that matter:

```csharp
var opts = new JobExecutionOptions { WaitTimeout = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(50) };
var timedOut = await jobs.CallAsync(CallOutcomeTimeoutJobs.SlowReport(), opts);
Console.WriteLine($"TimedOut: {timedOut.TimedOut}");
```

In `call-outcome-timeout.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

Console.WriteLine("--- (a) timeout ---");

var opts = new JobExecutionOptions { WaitTimeout = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(50) };
var timedOut = await jobs.CallAsync(CallOutcomeTimeoutJobs.SlowReport(), opts);
Console.WriteLine($"TimedOut: {timedOut.TimedOut}");

Console.WriteLine($"Succeeded: {timedOut.Succeeded}");

Console.WriteLine("--- (b) failure ---");

// A failing handler: Status is Failed, ThrowIfFailed throws JobFailedException, and TryGetValue is false.
var failed = await jobs.CallAsync(CallOutcomeTimeoutJobs.FailingReport());
Console.WriteLine($"Status: {failed.Status}");

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

// On success, ValueOrThrow returns the handler's result.
var success = await jobs.CallAsync(CallOutcomeTimeoutJobs.SuccessReport());
Console.WriteLine($"Succeeded: {success.Succeeded}");
var result = success.ValueOrThrow();

Console.WriteLine($"ValueOrThrow: {result.Summary}");
```

## 2. Read a job that failed

A failing handler: Status is Failed, ThrowIfFailed throws JobFailedException, and TryGetValue is false.

The lines that matter:

```csharp
var failed = await jobs.CallAsync(CallOutcomeTimeoutJobs.FailingReport());
Console.WriteLine($"Status: {failed.Status}");
```

In `call-outcome-timeout.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

Console.WriteLine("--- (a) timeout ---");

// A slow handler with a short WaitTimeout: outcome.TimedOut is true.
// The job keeps running on the worker; the caller just stops waiting.
var opts = new JobExecutionOptions { WaitTimeout = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(50) };
var timedOut = await jobs.CallAsync(CallOutcomeTimeoutJobs.SlowReport(), opts);
Console.WriteLine($"TimedOut: {timedOut.TimedOut}");

Console.WriteLine($"Succeeded: {timedOut.Succeeded}");

Console.WriteLine("--- (b) failure ---");

var failed = await jobs.CallAsync(CallOutcomeTimeoutJobs.FailingReport());
Console.WriteLine($"Status: {failed.Status}");

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

// On success, ValueOrThrow returns the handler's result.
var success = await jobs.CallAsync(CallOutcomeTimeoutJobs.SuccessReport());
Console.WriteLine($"Succeeded: {success.Succeeded}");
var result = success.ValueOrThrow();

Console.WriteLine($"ValueOrThrow: {result.Summary}");
```

## 3. Take the value of a job that succeeded

On success, ValueOrThrow returns the handler's result.

The lines that matter:

```csharp
var success = await jobs.CallAsync(CallOutcomeTimeoutJobs.SuccessReport());
Console.WriteLine($"Succeeded: {success.Succeeded}");
var result = success.ValueOrThrow();
```

In `call-outcome-timeout.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

Console.WriteLine("--- (a) timeout ---");

// A slow handler with a short WaitTimeout: outcome.TimedOut is true.
// The job keeps running on the worker; the caller just stops waiting.
var opts = new JobExecutionOptions { WaitTimeout = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromMilliseconds(50) };
var timedOut = await jobs.CallAsync(CallOutcomeTimeoutJobs.SlowReport(), opts);
Console.WriteLine($"TimedOut: {timedOut.TimedOut}");

Console.WriteLine($"Succeeded: {timedOut.Succeeded}");

Console.WriteLine("--- (b) failure ---");

// A failing handler: Status is Failed, ThrowIfFailed throws JobFailedException, and TryGetValue is false.
var failed = await jobs.CallAsync(CallOutcomeTimeoutJobs.FailingReport());
Console.WriteLine($"Status: {failed.Status}");

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

var success = await jobs.CallAsync(CallOutcomeTimeoutJobs.SuccessReport());
Console.WriteLine($"Succeeded: {success.Succeeded}");
var result = success.ValueOrThrow();

Console.WriteLine($"ValueOrThrow: {result.Summary}");
```

## 4. The job it acts on

`call-outcome-timeout.cs` (excerpt):

```csharp
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
```

## 5. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/300-failure-and-recovery/311-call-outcome-timeout
```

Its output must contain:

```text
TimedOut: True
Status: Failed
ThrowIfFailed threw: JobFailedException
TryGetValue: False
ValueOrThrow: all clear
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to start child jobs and wait for them](/learn/211-child-jobs.md)

## More

- [The whole program](/learn/311-call-outcome-timeout.program.md): every file as compiled, 102 lines.
