# How to get an alert when a job fails

A failing job raises an alert by itself, with no `AlertAsync` in the handler. `AlertProfile` on the job says when, and repeats of the same job's failure count up on one alert rather than opening new ones.

- Section: Get told when something breaks, step 1
- Program: `concepts/400-observability-and-alerts/406-automatic-failure-alerts` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/406-automatic-failure-alerts
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Say on the job when its failures alert, and where

AlertProfile = OnFailure + AlertChannelName routes automatic alerts to the "default" log channel. RunbookUrl surfaces on the delivered alert log line (LogAlertTransport prints runbook=...), not on list queries. Backoff = 0s skips wait between retries; the 2 s delay in the handler gives the driver a window to cancel during the third attempt.

The lines that matter:

```csharp
[Job(
    "process-report",
    AlertProfile = AlertProfileCode.OnFailure,
    AlertChannelName = "default",
    RunbookUrl = "https://runbooks.example.com/process-report",
    MaxAttempts = 10,
    Backoff = "0s"
)]
```

In `automatic-failure-alerts.cs` (excerpt):

```csharp
public sealed record ProcessReport(string ReportId);

public static class ProcessReportJob
{
    [Job(
        "process-report",
        AlertProfile = AlertProfileCode.OnFailure,
        AlertChannelName = "default",
        RunbookUrl = "https://runbooks.example.com/process-report",
        MaxAttempts = 10,
        Backoff = "0s"
    )]
    public static async Task Handle(ProcessReport input, CancellationToken ct)
    {
        await Task.Delay(2000, ct);
        throw new InvalidOperationException($"Report store unavailable for '{input.ReportId}'.");
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/400-observability-and-alerts/406-automatic-failure-alerts
```

Its output must contain:

```text
Cancelling job to stay in the FirstFailure stage...
alert kind=FirstFailure severity=Warning occurrences=2 channel='default'
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- The alert is not instant: `sys.alerts` turns failure events into alerts once a minute, so one appears up to about two minutes after the failure.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to raise an alert from a handler](/learn/402-alerts.md)

## More

- [The whole program](/learn/406-automatic-failure-alerts.program.md): every file as compiled, 111 lines.
