# How to escalate an alert as failures add up

Automatic failure alerts escalate in stages: the first failure, the failure count reaching `AlertFailureThreshold`, and the final failure when the retry budget is spent.

- Section: Get told when something breaks, step 5
- Program: `learn/400-observability-and-alerts/411-alert-escalation` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/411-alert-escalation
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Set the failure count that escalates

AlertFailureThreshold = 2: the ThresholdReached alert fires when OccurrenceCount reaches 2.

The lines that matter:

```csharp
j.ConfigureOptions(o => o.AlertFailureThreshold = 2);
```

In `alert-escalation.cs` (excerpt):

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    j.ConfigureOptions(o => o.AlertFailureThreshold = 2);

    j.Run<AlertEscalationJobs>("alert-escalation");
});

using var host = builder.Build();
await host.StartAsync();
```

## 2. Read each stage as an alert of its own kind

The `sys.alerts` sweep writes them once a minute, so they appear after the failures, not with them.

The lines that matter:

```csharp
foreach (var alert in alertItems.OrderBy(a => a.CreatedAtUtc))
{
    Console.WriteLine($"  [{alert.Severity}] {alert.Kind} x{alert.OccurrenceCount} channel='{alert.ChannelName}'");
}
```

In `alert-escalation.cs` (excerpt):

```csharp
Console.WriteLine($"alerts ({alertItems.Count}):");

foreach (var alert in alertItems.OrderBy(a => a.CreatedAtUtc))
{
    Console.WriteLine($"  [{alert.Severity}] {alert.Kind} x{alert.OccurrenceCount} channel='{alert.ChannelName}'");
}

await host.StopAsync();
```

## 3. The job it acts on

`alert-escalation.cs` (excerpt):

```csharp
public sealed class UnstableReportJob
{
    // AlertProfile = OnFailure (default): automatic failure alerts on every retryable failure
    // and a separate FinalFailure alert when the attempt budget is exhausted.
    [Job("unstable-report", MaxAttempts = 4, Backoff = "0s")]
    public static Task Handle(UnstableReport input, CancellationToken ct)
    {
        Console.WriteLine($"[{input.Name}] attempt failed");
        throw new InvalidOperationException($"report {input.Name} unavailable");
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/400-observability-and-alerts/411-alert-escalation
```

Its output must contain:

```text
job reached Failed
] FirstFailure x
] ThresholdReached x
] FinalFailure x
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to test a handler with the test host](/learn/801-testing-jobs.md)

## More

- [The whole program](/learn/411-alert-escalation.program.md): every file as compiled, 87 lines.
