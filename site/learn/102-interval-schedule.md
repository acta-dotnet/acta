# How to replace a timer loop with an interval schedule

An interval schedule, such as `10s`, runs a job at a fixed gap, where a timer loop in a hosted service would otherwise stand.

- Section: Run work on a schedule, step 3
- Program: `concepts/100-scheduling/102-interval-schedule` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/102-interval-schedule
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Give the schedule an interval instead of a cron

A schedule can be cron or an interval duration - the human form ("10s") or ISO 8601 ("PT10S", and calendar forms like "P1D"). An interval has no wall-clock anchor, so it ignores TimeZoneId.

The lines that matter:

```csharp
[Schedule("every-10-seconds", "10s")]
```

In `interval-schedule.cs` (excerpt):

```csharp
public readonly record struct Heartbeat;

public sealed class HeartbeatJob
{
    [Job("heartbeat")]
    [Schedule("every-10-seconds", "10s")]
    public void Handle(Heartbeat input) => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC heartbeat");
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/100-scheduling/102-interval-schedule
```

Its output must contain:

```text
 heartbeat
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to give one job several schedules](/learn/103-multiple-schedules.md)

## More

- [The whole program](/learn/102-interval-schedule.program.md): every file as compiled, 33 lines.
