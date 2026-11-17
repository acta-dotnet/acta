# How to run a job every day at a set hour in your time zone

A cron with `TimeZoneId` fires at the same local hour all year: `0 8 * * *` in `Europe/Berlin` is 08:00 in Berlin, in summer and in winter.

- Section: Run work on a schedule, step 2
- Program: `learn/100-scheduling/104-timezone-schedule` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/104-timezone-schedule
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write the hour as a cron and pin it to your time zone

The cron has five fields: minute, hour, day of month, month, day of week. `0 8 * * *` is 08:00 every day, and without a time zone that hour is read as UTC. TimeZoneId pins cron to wall-clock local time across DST switches (IANA or Windows ids; an unresolvable id fails the deploy, which a development start runs). For Berlin write `Europe/Berlin`.

The lines that matter:

```csharp
[Schedule("morning-ljubljana", "0 8 * * *", TimeZoneId = "Europe/Ljubljana")]
```

In `timezone-schedule.cs` (excerpt):

```csharp
public sealed class DailyDigestJob
{
    [Job("daily-digest")]
    [Schedule("morning-ljubljana", "0 8 * * *", TimeZoneId = "Europe/Ljubljana")]
    // The second schedule is here so the program prints while you watch. Intervals are absolute gaps and take no timezone.
    [Schedule("demo-tick", "PT15S")]
    public Task Handle(DailyDigest input, JobContext context, CancellationToken ct)
    {
        Console.WriteLine(
            $"{DateTime.UtcNow:HH:mm:ss} UTC fired by [{string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName))}]"
        );
        return Task.CompletedTask;
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/104-timezone-schedule
```

Its output must contain:

```text
fired by [demo-tick]
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A schedule is written by a deploy: `jobs migrate` or `jobs deploy` in production, the development start on your own machine. Once a namespace has been deployed that way, a new or changed schedule takes `jobs deploy`.
- A permanent job keeps its step results from one firing to the next. Put the firing in a step's name, `$"send.f{ctx.FiringId:N}"`, or the step that ran on Monday answers from its record on Tuesday.
- A run that comes due while no worker is up is skipped by default. [Decide what happens to runs a schedule missed](/learn/106-schedule-misfire.md) shows the other choices.
- On the night the clocks change, a cron that names an hour they skip fires at the moment they jump, and one that names an hour they repeat fires once, in its first pass. An hour outside 01:00 to 03:00 local time meets neither in most zones.
- A time zone id is an IANA or a Windows id. One that cannot be resolved fails the deploy, so a typo never ships a schedule that fires at the wrong hour.
- An interval such as `"PT15S"` or `"10s"` is a fixed gap between firings and takes no time zone.
- The handler does its bounded work and returns. Never loop or sleep inside it to wait for the next day: the schedule brings the next firing.
- When each night is its own piece of business, with its own retries and result, [make every occurrence its own job](/learn/110-per-occurrence-delivery.md).
- `Cron.Hourly`, `Cron.DailyAt9`, `Cron.EveryMonday`, and `Cron.Every15Minutes` name common crons, so the usual cases need no cron string.
- The line the release check reads comes from `demo-tick`, the interval schedule. The 08:00 schedule would take a day to show.

## Check yourself

1. The schedule is `"0 8 * * *"` with `TimeZoneId = "Europe/Berlin"`. In July Berlin is two hours ahead of UTC. When does the job fire, in UTC?
   - a) 06:00
   - b) 07:00
   - c) 08:00
   - Answer: a. The cron is read on the Berlin wall clock, so 08:00 there is 06:00 UTC in summer and 07:00 UTC in winter. The local hour stays the same all year.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to replace a timer loop with an interval schedule](/learn/102-interval-schedule.md)

## More

- [The whole program](/learn/104-timezone-schedule.program.md): every file as compiled, 49 lines.
