# How to choose how many events a job records

`EventRecording` on the job decides which events it writes. `Audit`, the default, records every event. `Failures` records the failed and orphaned attempts, the success that recovers from a failure, what an enabled alert profile reads, a caller's controls of the job, and a schedule's pauses and manual triggers.

- Section: Watch and control running work, step 14
- Program: `concepts/400-observability-and-alerts/407-event-recording` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/407-event-recording
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Record only failures for a job that runs often

Failures: a failure writes an event, and so does the success that answers one; a run that succeeds after no failure leaves none. Use it for high-frequency definitions to keep events lean.

The lines that matter:

```csharp
[Job("failures-work", EventRecording = EventRecordingCode.Failures)]
```

In `event-recording.cs` (excerpt):

```csharp
public sealed record AuditWork(string Id);

public sealed record FailuresWork(string Id);

public static class AuditWorkJob
{
    // Audit (default): a finished event is written for every execution.
    [Job("audit-work", EventRecording = EventRecordingCode.Audit)]
    public static void Handle(AuditWork input) => Console.WriteLine($"[{input.Id}] running at Audit level");
}

public static class FailuresWorkJob
{
    [Job("failures-work", EventRecording = EventRecordingCode.Failures)]
    public static void Handle(FailuresWork input) => Console.WriteLine($"[{input.Id}] running at Failures level");
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/400-observability-and-alerts/407-event-recording
```

Its output must contain:

```text
Audit level  -> event count: 1
Failures level -> event count: 0
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to get an alert when a job fails](/learn/406-automatic-failure-alerts.md)

## More

- [The whole program](/learn/407-event-recording.program.md): every file as compiled, 62 lines.
